using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Apex.Desktop.Services;
using Microsoft.Win32.SafeHandles;

namespace Apex.Desktop.Okx;

// Scope-local store only. Provisioning has no startup caller; Stage6 must supply
// protected provisioning. CurrentUser DPAPI protects storage, not activation.
internal sealed class WindowsOkxCredentialStore : IOkxExecutionSecretProvider
{
    private readonly string admittedDataRoot;
    internal WindowsOkxCredentialStore(string admittedDataRoot) { this.admittedDataRoot = admittedDataRoot; }
    public OkxExecutionCredentialLease Open(OkxScopeBinding binding, OkxScopePaths paths)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("OKX_CREDENTIAL_UNAVAILABLE");
        byte[]? plaintext = null;
        try
        {
            using var root = Admit(binding, paths);
            ValidateScopeLayout(paths);
            using var file = new FileStream(paths.Credentials, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var metadata = WindowsPathSecurity.Open(paths.Credentials);
            WindowsPathSecurity.Validate(metadata, true);
            if (metadata.IsDirectory || !GetFileInformationByHandle(metadata.Handle, out var info) || info.Links != 1 || file.Length is <= 0 or > 65536) throw Invalid();
            byte[] encrypted = new byte[(int)file.Length]; file.ReadExactly(encrypted);
            if (file.ReadByte() != -1) throw Invalid();
            plaintext = Crypt(encrypted, Entropy(root.Path, binding), false);
            return OkxCredentialEnvelope.Decode(plaintext, binding);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw Invalid(); }
        finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
    }
    internal void Create(OkxScopeBinding binding, OkxScopePaths paths, string provenance, byte[] key, byte[] secret, byte[] passphrase)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("OKX_CREDENTIAL_UNAVAILABLE");
        byte[]? plaintext = null;
        try
        {
            // Caller keeps its buffers; the temporary lease deliberately owns copies.
            if (key is not { Length: > 0 and <= 4096 } || secret is not { Length: > 0 and <= 4096 } || passphrase is not { Length: > 0 and <= 4096 }) throw Invalid();
            using var lease = new OkxExecutionCredentialLease(binding, provenance, key.ToArray(), secret.ToArray(), passphrase.ToArray());
            using var root = Admit(binding, paths);
            if (Exists(paths.Root)) throw Invalid(); // no repair, overwrites or partial-state retry
            ValidateParents(paths);
            plaintext = OkxCredentialEnvelope.Encode(lease);
            byte[] encrypted = Crypt(plaintext, Entropy(root.Path, binding), true);
            var held = new List<WindowsPathSecurity.NativeObject>();
            try
            {
                foreach (string directory in new[] { Path.Combine(root.Path, "okx"), Path.GetDirectoryName(paths.Root)!, paths.Root })
                {
                    if (!Exists(directory)) WindowsPathSecurity.CreatePrivateDirectory(directory);
                    else if (directory == paths.Root) throw Invalid();
                    var opened = WindowsPathSecurity.Open(directory); held.Add(opened);
                    if (!opened.IsDirectory || !opened.Path.Equals(directory, StringComparison.OrdinalIgnoreCase)) throw Invalid();
                    WindowsPathSecurity.Validate(opened, true);
                }
                // Only ciphertext is written. Pending is durable unexplained partial
                // state after interruption, never silently repaired on next startup.
                string pending = paths.Credentials + ".pending";
                using (var file = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { file.Write(encrypted); file.Flush(flushToDisk: true); }
                File.Move(pending, paths.Credentials, overwrite: false);
                using var final = WindowsPathSecurity.Open(paths.Credentials);
                WindowsPathSecurity.Validate(final, true);
            }
            finally { foreach (var handle in held.AsEnumerable().Reverse()) handle.Dispose(); }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { throw Invalid(); }
        finally { if (plaintext != null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static InvalidDataException Invalid() => new("OKX_CREDENTIAL_INVALID");
    [SupportedOSPlatform("windows")]
    private WindowsPrivateDataLease Admit(OkxScopeBinding binding, OkxScopePaths paths)
    {
        if (paths != OkxConfiguration.Paths(admittedDataRoot, binding) || !Directory.Exists(admittedDataRoot)) throw Invalid();
        var root = WindowsPrivateData.Open(admittedDataRoot, AppContext.BaseDirectory);
        try
        {
            // Reject aliases instead of silently moving one scope to another root.
            if (!root.Path.Equals(Path.TrimEndingDirectorySeparator(admittedDataRoot), StringComparison.OrdinalIgnoreCase)) throw Invalid();
            return root;
        }
        catch { root.Dispose(); throw; }
    }
    [SupportedOSPlatform("windows")]
    private void ValidateParents(OkxScopePaths paths)
    {
        string okx = Path.Combine(admittedDataRoot, "okx");
        if (Exists(okx))
            foreach (string entry in Directory.EnumerateFileSystemEntries(okx))
                if (!Directory.Exists(entry) || Path.GetFileName(entry) is not ("demo" or "live")) throw Invalid();
        string environment = Path.GetDirectoryName(paths.Root)!;
        if (Exists(environment))
            foreach (string entry in Directory.EnumerateFileSystemEntries(environment))
                if (!Directory.Exists(entry) || !OkxConfiguration.Identifier(Path.GetFileName(entry))) throw Invalid();
    }
    [SupportedOSPlatform("windows")]
    private void ValidateScopeLayout(OkxScopePaths paths)
    {
        ValidateParents(paths);
        if (!Directory.Exists(paths.Root) || !File.Exists(paths.Credentials)) throw Invalid();
        foreach (string entry in Directory.EnumerateFileSystemEntries(paths.Root))
        {
            if (entry == paths.Credentials) continue;
            // Known future authority/publication trees are only path-admitted here;
            // this store neither opens financial state nor asserts its completeness.
            if ((entry != paths.Authority && entry != paths.Publication) || !Directory.Exists(entry)) throw Invalid();
        }
    }
    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    [SupportedOSPlatform("windows")]
    private static byte[] Entropy(string root, OkxScopeBinding binding)
    {
        using var opened = WindowsPathSecurity.Open(root);
        if (!opened.IsDirectory || !GetFileInformationByHandle(opened.Handle, out var identity)) throw Invalid();
        // Native directory identity prevents copied blobs surviving root recreation.
        return SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Schema = OkxCredentialEnvelope.Schema, Root = opened.Path.ToUpperInvariant(), identity.VolumeSerial, identity.IndexHigh, identity.IndexLow, Binding = binding }));
    }
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { internal int Length; internal IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out WindowsPathSecurity.FileInformation info);
    private static byte[] Crypt(byte[] bytes, byte[] purpose, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        DataBlob entropy = default, output = default;
        try
        {
            entropy = new DataBlob { Length = purpose.Length, Data = Marshal.AllocHGlobal(purpose.Length) };
            Marshal.Copy(bytes, 0, input.Data, bytes.Length); Marshal.Copy(purpose, 0, entropy.Data, purpose.Length);
            // CRYPTPROTECT_UI_FORBIDDEN only. No LOCAL_MACHINE or interactive UI.
            bool ok = protect ? CryptProtectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok || output.Length is <= 0 or > 65536) throw Invalid();
            byte[] result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            Zero(input); Marshal.FreeHGlobal(input.Data);
            if (entropy.Data != IntPtr.Zero) { Zero(entropy); Marshal.FreeHGlobal(entropy.Data); }
            if (output.Data != IntPtr.Zero) { Zero(output); LocalFree(output.Data); }
            CryptographicOperations.ZeroMemory(purpose);
        }
    }
    private static void Zero(DataBlob blob) { for (int i = 0; i < blob.Length; i++) Marshal.WriteByte(blob.Data, i, 0); }
}
