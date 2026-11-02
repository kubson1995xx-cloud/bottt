using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Apex.Adapters.Okx.Execution;
namespace Apex.Desktop.Okx;

// One submit, not an activation grant. The controller's STOP/generation lock and
// every cancellation callback serialize with the final no-replace rename only.
internal sealed class OkxReadCommit : IDisposable
{
    private readonly object sync;
    private readonly Func<bool> current;
    private readonly CancellationToken token;
    private readonly CancellationTokenRegistration cancellation;
    private readonly OkxEnrollmentObservation observed;
    private readonly byte[][] values;
    private readonly byte[] binding;
    private readonly Action<OkxReadAccount> saved;
    private readonly Func<DateTimeOffset> utc;
    private readonly Func<long> ticks;
    private readonly DateTimeOffset began;
    private readonly long beganTicks;
    private bool approved,revoked;
    internal Guid Request { get; }=Guid.NewGuid();
    internal DateTimeOffset Expires { get; }
    internal OkxReadAccount? Account { get; private set; }
    internal OkxReadCommit(object sync,Func<bool> current,CancellationToken token,OkxEnrollmentObservation observed,byte[][] values,Action<OkxReadAccount> saved,Func<DateTimeOffset>? utc=null,Func<long>? ticks=null)
    {
        this.sync=sync;this.current=current;this.token=token;this.observed=observed;this.values=values;this.saved=saved;
        this.utc=utc??(()=>DateTimeOffset.UtcNow);this.ticks=ticks??(()=>Environment.TickCount64);
        began=this.utc();beganTicks=this.ticks();Expires=began.AddSeconds(120);binding=Digest(values);
        cancellation=token.Register(()=>{lock(sync)revoked=true;});
    }
    // Called only after ConfirmDetailedAsync accepted the exact displayed request.
    internal void Approve(Guid request){lock(sync){if(request!=Request)throw new InvalidDataException("CONNECT_APPROVAL_BINDING");Ensure(observed,values,false);approved=true;}}
    internal void Check(OkxEnrollmentObservation observation,byte[][] pending){lock(sync)Ensure(observation,pending,true);}
    private void Ensure(OkxEnrollmentObservation observation,byte[][] pending,bool needApproval)
    {
        token.ThrowIfCancellationRequested();
        var now=utc();long tick=ticks();
        if(revoked||Account!=null||!current()||needApproval&&!approved||now<began||now>=Expires||tick<beganTicks||tick-beganTicks>=120000||observation!=observed||observation.Environment!="Demo"||observation.Provenance!="DemoTradingApi")throw new InvalidDataException("CONNECT_APPROVAL_EXPIRED_OR_BINDING_CHANGED");
        byte[] actual=Digest(pending);
        try{if(!CryptographicOperations.FixedTimeEquals(binding,actual)||observation.Fingerprint!=Convert.ToHexString(SHA256.HashData(pending[0])).ToLowerInvariant())throw new InvalidDataException("CONNECT_APPROVAL_EXPIRED_OR_BINDING_CHANGED");}
        finally{CryptographicOperations.ZeroMemory(actual);}
    }
    internal OkxReadAccount Publish(OkxEnrollmentObservation observation,byte[][] pending,OkxReadAccount account,Action rename)
    {
        lock(sync)
        {
            Ensure(observation,pending,true);
            if(account.Uid!=observed.Uid||account.MainUid!=observed.MainUid||account.Environment!=observed.Environment||account.Provenance!=observed.Provenance||account.Fingerprint!=observed.Fingerprint)throw new InvalidDataException("CONNECT_APPROVAL_BINDING");
            // Linearization: fully flushed/verified ciphertext becomes the final
            // credential by one no-clobber rename. Cancellation cannot undo it.
            rename();Account=account;saved(account);return account;
        }
    }
    private static byte[] Digest(byte[][] pending)
    {
        if(pending.Length!=3)throw new InvalidDataException("READ_STORE_FIELD");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length=stackalloc byte[4];
        foreach(var value in pending){if(value==null||value.Length is <=0 or >4096)throw new InvalidDataException("READ_STORE_FIELD");System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length,value.Length);hash.AppendData(length);hash.AppendData(value);}
        return hash.GetHashAndReset();
    }
    public void Dispose(){cancellation.Dispose();CryptographicOperations.ZeroMemory(binding);}
}
