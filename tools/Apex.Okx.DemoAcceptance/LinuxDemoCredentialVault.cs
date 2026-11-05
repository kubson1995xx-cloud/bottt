using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Apex.Okx.DemoAcceptance;

public enum DemoVaultState { NotStored, Stored, Unavailable }
public enum DemoVaultError { NotStored, AlreadyStored, PathRefused, InvalidCredential, InvalidEnvelope, NativeUnavailable, Cancelled, StorageUnavailable, CommitUncertain }
// Codes only. Never attach native stderr, paths, exception text or secret-bearing inner exceptions.
public sealed class DemoVaultException(DemoVaultError code) : IOException("DEMO_VAULT_" + code.ToString().ToUpperInvariant()) { public DemoVaultError Code { get; } = code; }
public sealed record DemoVaultStatus(DemoVaultState State)
{
    public string Schema => "apex-okx-demo-credentials-v1";
    public string Provenance => "DemoTradingApi";
}

/// <summary>Single-owner, non-thread-safe credential lifetime. Dispose or transfer exactly once.</summary>
public sealed class DemoCredentialBundle : IDisposable
{
    private SensitiveBuffer? key, secret, pass;
    internal DemoCredentialBundle(SensitiveBuffer k, SensitiveBuffer s, SensitiveBuffer p) { key=k;secret=s;pass=p; }
    public string Provenance => "DemoTradingApi";
    public ReadOnlyMemory<byte> ApiKey => Alive(key).Memory;
    public ReadOnlyMemory<byte> SecretKey => Alive(secret).Memory;
    public ReadOnlyMemory<byte> Passphrase => Alive(pass).Memory;
    private static SensitiveBuffer Alive(SensitiveBuffer? b) => b ?? throw new ObjectDisposedException(nameof(DemoCredentialBundle));
    /// <summary>Factory must synchronously take exclusive ownership; on throw it must not retain references.
    /// A successful owner MUST zero all three byte arrays on disposal. No string conversion is needed.</summary>
    public T TransferTo<T>(Func<byte[], byte[], byte[], T> factory) where T : class
    {
        ArgumentNullException.ThrowIfNull(factory);
        var k=Alive(key);var s=Alive(secret);var p=Alive(pass);
        try
        {
            T result=factory(k.Array,s.Array,p.Array) ?? throw new InvalidOperationException("DEMO_CREDENTIAL_OWNER_REQUIRED");
            k.Detach();s.Detach();p.Detach();key=secret=pass=null;return result;
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { key?.Dispose();secret?.Dispose();pass?.Dispose();key=secret=pass=null; }
}

/// <summary>Linux x64 only; ciphertext at rest, not financial/trading authority.
/// Public construction pins the production root. Internal construction is for isolated friend harnesses.</summary>
public sealed class LinuxDemoCredentialVault
{
    public const string ProductionRoot = "/home/ubuntu/apex-private/okx-demo-credentials";
    public const string RecordName = "credentials.cred";
    public const int MaximumFieldBytes = 4096;
    internal const int MaximumEnvelopeBytes = 32+3*MaximumFieldBytes;
    internal const int MaximumCiphertextBytes = 32768;
    internal const string NativeName = "apex-okx-demo-credentials-v1";
    private readonly string root;
    private readonly IBufferAllocator allocator;
    private readonly ICredentialCryptor cryptor;
    public LinuxDemoCredentialVault() : this(ProductionRoot) { }
    internal LinuxDemoCredentialVault(string root, IBufferAllocator? allocator=null, ICredentialCryptor? cryptor=null)
    { this.root=root;this.allocator=allocator??HeapBufferAllocator.Instance;this.cryptor=cryptor??new SystemdCredentialCryptor(this.allocator); }

    // Ciphertext identity only; never decrypts or returns material. Caller holds shared run lock.
    internal Task<string> RecordIdentityAsync(CancellationToken ct=default)=>Guard(async token=>
    {
        using var dir=await VaultDirectory.OpenAsync(root,false,token).ConfigureAwait(false)??throw Error(DemoVaultError.NotStored);
        using var encrypted=dir.ReadRecord(allocator);
        return Convert.ToHexString(SHA256.HashData(encrypted.Memory.Span)).ToLowerInvariant();
    },ct);
    public async Task<DemoVaultStatus> StatusAsync(CancellationToken ct=default)
    {
        try { using var b=await LoadAsync(ct).ConfigureAwait(false);return new(DemoVaultState.Stored); }
        catch(DemoVaultException e) { return new(e.Code==DemoVaultError.NotStored?DemoVaultState.NotStored:DemoVaultState.Unavailable); }
    }
    // Input belongs to caller and must be cleared by caller, including on failure.
    public Task SaveNewAsync(ReadOnlyMemory<byte> key,ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,CancellationToken ct=default) => SaveAsync(key,secret,passphrase,false,ct);
    // Explicit intent; never silently rotate on decrypt/service failure.
    public Task ReplaceAsync(ReadOnlyMemory<byte> key,ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,CancellationToken ct=default) => SaveAsync(key,secret,passphrase,true,ct);
    private Task SaveAsync(ReadOnlyMemory<byte> key,ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,bool replace,CancellationToken ct) => Guard(async token=>
    {
        ValidateField(key.Span,DemoVaultError.InvalidCredential);ValidateField(secret.Span,DemoVaultError.InvalidCredential);ValidateField(passphrase.Span,DemoVaultError.InvalidCredential);
        using var plain=new SensitiveBuffer(allocator,MaximumEnvelopeBytes);
        Encode(plain,key,secret,passphrase);
        using var dir=await VaultDirectory.OpenAsync(root,true,token).ConfigureAwait(false) ?? throw Error(DemoVaultError.PathRefused);
        var old=dir.InspectRecord();
        if(!replace&&old!=null)throw Error(DemoVaultError.AlreadyStored);
        if(replace&&old==null)throw Error(DemoVaultError.NotStored);
        using var encrypted=await cryptor.TransformAsync(true,plain.Memory,token).ConfigureAwait(false);
        if(encrypted.Length is <1 or >MaximumCiphertextBytes)throw Error(DemoVaultError.NativeUnavailable);
        // Verify preparation before changing any persistent record.
        using var verified=await cryptor.TransformAsync(false,encrypted.Memory,token).ConfigureAwait(false);
        ValidateEnvelope(verified.Memory.Span);
        if(!CryptographicOperations.FixedTimeEquals(verified.Memory.Span,plain.Memory.Span))throw Error(DemoVaultError.NativeUnavailable);
        token.ThrowIfCancellationRequested();
        dir.Publish(encrypted.Memory.Span,old,replace,token);
        return true;
    },ct);
    public Task<DemoCredentialBundle> LoadAsync(CancellationToken ct=default) => Guard(async token=>
    {
        using var dir=await VaultDirectory.OpenAsync(root,false,token).ConfigureAwait(false) ?? throw Error(DemoVaultError.NotStored);
        using var encrypted=dir.ReadRecord(allocator);
        using var plain=await cryptor.TransformAsync(false,encrypted.Memory,token).ConfigureAwait(false);
        ValidateEnvelope(plain.Memory.Span);
        SensitiveBuffer? k=null,s=null,p=null;
        try
        {
            int kl=(int)BinaryPrimitives.ReadUInt32LittleEndian(plain.Memory.Span[20..]);
            int sl=(int)BinaryPrimitives.ReadUInt32LittleEndian(plain.Memory.Span[24..]);
            int pl=(int)BinaryPrimitives.ReadUInt32LittleEndian(plain.Memory.Span[28..]);
            k=new(allocator,kl);s=new(allocator,sl);p=new(allocator,pl);
            plain.Memory.Slice(32,kl).CopyTo(k.Array);k.Length=kl;
            plain.Memory.Slice(32+kl,sl).CopyTo(s.Array);s.Length=sl;
            plain.Memory.Slice(32+kl+sl,pl).CopyTo(p.Array);p.Length=pl;
            token.ThrowIfCancellationRequested();
            var result=new DemoCredentialBundle(k,s,p);k=s=p=null;return result;
        }
        finally { k?.Dispose();s?.Dispose();p?.Dispose(); }
    },ct);
    public Task<bool> ForgetAsync(CancellationToken ct=default) => Guard(async token=>
    {
        using var dir=await VaultDirectory.OpenAsync(root,false,token).ConfigureAwait(false);
        if(dir==null)return false;
        return dir.Forget(token);
    },ct);
    private static async Task<T> Guard<T>(Func<CancellationToken,Task<T>> body,CancellationToken ct)
    {
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if(!OperatingSystem.IsLinux()||RuntimeInformation.ProcessArchitecture!=Architecture.X64)throw Error(DemoVaultError.PathRefused);
            budget.Token.ThrowIfCancellationRequested();return await body(budget.Token).ConfigureAwait(false);
        }
        catch(DemoVaultException) { throw; }
        catch(OperationCanceledException) { throw Error(DemoVaultError.Cancelled); }
        catch { throw Error(DemoVaultError.StorageUnavailable); }
    }
    internal static DemoVaultException Error(DemoVaultError code) => new(code);
    internal static void ValidateField(ReadOnlySpan<byte> field,DemoVaultError code)
    { if(field.Length is <4 or >MaximumFieldBytes)throw Error(code);foreach(byte b in field)if(b is <33 or >126)throw Error(code); }
    private static void Encode(SensitiveBuffer b,ReadOnlyMemory<byte> key,ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> pass)
    {
        "APXOKXD1"u8.CopyTo(b.Array);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(8),1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(12),1); // DEMO, not a configurable environment.
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(16),1); // DemoTradingApi only.
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(20),(uint)key.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(24),(uint)secret.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.Array.AsSpan(28),(uint)pass.Length);
        key.CopyTo(b.Array.AsMemory(32));secret.CopyTo(b.Array.AsMemory(32+key.Length));pass.CopyTo(b.Array.AsMemory(32+key.Length+secret.Length));
        b.Length=32+key.Length+secret.Length+pass.Length;
    }
    internal static void ValidateEnvelope(ReadOnlySpan<byte> b)
    {
        if(b.Length is <44 or >MaximumEnvelopeBytes||!b[..8].SequenceEqual("APXOKXD1"u8)||BinaryPrimitives.ReadUInt32LittleEndian(b[8..])!=1||BinaryPrimitives.ReadUInt32LittleEndian(b[12..])!=1||BinaryPrimitives.ReadUInt32LittleEndian(b[16..])!=1)throw Error(DemoVaultError.InvalidEnvelope);
        uint k=BinaryPrimitives.ReadUInt32LittleEndian(b[20..]),s=BinaryPrimitives.ReadUInt32LittleEndian(b[24..]),p=BinaryPrimitives.ReadUInt32LittleEndian(b[28..]);
        if(k is <4 or >MaximumFieldBytes||s is <4 or >MaximumFieldBytes||p is <4 or >MaximumFieldBytes||32+k+s+p!=b.Length)throw Error(DemoVaultError.InvalidEnvelope);
        ValidateField(b.Slice(32,(int)k),DemoVaultError.InvalidEnvelope);ValidateField(b.Slice(32+(int)k,(int)s),DemoVaultError.InvalidEnvelope);ValidateField(b.Slice(32+(int)k+(int)s,(int)p),DemoVaultError.InvalidEnvelope);
    }
}

// Internal allocation abstraction permits bounded allocations and deterministic fault injection.
// The owner, not the allocator, zeroes the entire capacity before release.
internal interface IBufferAllocator { byte[] Allocate(int capacity);void Release(byte[] buffer); }
internal sealed class HeapBufferAllocator : IBufferAllocator
{
    internal static readonly HeapBufferAllocator Instance=new();
    public byte[] Allocate(int capacity)=>new byte[capacity];
    public void Release(byte[] buffer) { }
}
internal sealed class SensitiveBuffer : IDisposable
{
    private byte[]? data;
    private readonly IBufferAllocator allocator;
    internal SensitiveBuffer(IBufferAllocator allocator,int capacity) { this.allocator=allocator;data=allocator.Allocate(capacity); }
    internal byte[] Array=>data??throw new ObjectDisposedException(nameof(SensitiveBuffer));
    internal int Length {get;set;}
    internal ReadOnlyMemory<byte> Memory=>Array.AsMemory(0,Length);
    internal void Detach() {data=null;Length=0;}
    public void Dispose() { var b=data;data=null;Length=0;if(b==null)return;CryptographicOperations.ZeroMemory(b);allocator.Release(b); }
}
internal interface ICredentialCryptor { Task<SensitiveBuffer> TransformAsync(bool encrypt,ReadOnlyMemory<byte> input,CancellationToken ct); }
