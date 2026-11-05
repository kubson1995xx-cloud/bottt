using System.Diagnostics;
using static Apex.Okx.DemoAcceptance.LinuxDemoCredentialVault;

namespace Apex.Okx.DemoAcceptance;

internal interface ICredentialProcessLauncher { Process Start(ProcessStartInfo info); }
internal sealed class NativeCredentialProcessLauncher : ICredentialProcessLauncher
{
    internal static readonly NativeCredentialProcessLauncher Instance=new();
    public Process Start(ProcessStartInfo info)=>Process.Start(info)??throw Error(DemoVaultError.NativeUnavailable);
}
internal sealed class SystemdCredentialCryptor(IBufferAllocator allocator,ICredentialProcessLauncher? launcher=null) : ICredentialCryptor
{
    public async Task<SensitiveBuffer> TransformAsync(bool encrypt,ReadOnlyMemory<byte> input,CancellationToken ct)
    {
        SensitiveBuffer? output=null;
        try
        {
            ct.ThrowIfCancellationRequested();
            int cap=encrypt?MaximumCiphertextBytes:MaximumEnvelopeBytes;
            output=new(allocator,cap+1); // One guard byte detects overflow. Never grow/copy abandoned plaintext arrays.
            using var errorDrain=new SensitiveBuffer(allocator,4096);
            var info=new ProcessStartInfo("/usr/bin/systemd-creds")
            {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
                WorkingDirectory="/home/ubuntu"
            };
            info.Environment.Clear();info.Environment["HOME"]="/home/ubuntu";info.Environment["PATH"]="/usr/bin:/bin";info.Environment["LANG"]="C.UTF-8";
            info.ArgumentList.Add("--user");
            if(encrypt)info.ArgumentList.Add("--with-key=host");
            info.ArgumentList.Add("--name="+NativeName);
            if(!encrypt)info.ArgumentList.Add("--refuse-null");
            info.ArgumentList.Add("--no-ask-password");info.ArgumentList.Add(encrypt?"encrypt":"decrypt");info.ArgumentList.Add("-");info.ArgumentList.Add("-");
            using var process=(launcher??NativeCredentialProcessLauncher.Instance).Start(info);
            using var budget=CancellationTokenSource.CreateLinkedTokenSource(ct);budget.CancelAfter(TimeSpan.FromSeconds(15));
            void Kill() {try { if(!process.HasExited)process.Kill(entireProcessTree:true); }catch { /* No diagnostic text. */ } }
            using var registration=budget.Token.Register(Kill);
            async Task GuardPipe(Func<Task> body)
            { try {await body().ConfigureAwait(false);}catch {Kill();budget.Cancel();throw;} }
            var write=GuardPipe(async()=>
            {
                // BaseStream avoids StreamWriter's immutable strings/character encoder.
                try {await process.StandardInput.BaseStream.WriteAsync(input,budget.Token).ConfigureAwait(false);await process.StandardInput.BaseStream.FlushAsync(budget.Token).ConfigureAwait(false);}
                finally {process.StandardInput.Close();}
            });
            var read=GuardPipe(async()=>
            {
                int used=0;
                while(true)
                {
                    int n=await process.StandardOutput.BaseStream.ReadAsync(output.Array.AsMemory(used,cap+1-used),budget.Token).ConfigureAwait(false);
                    if(n==0)break;used+=n;
                    if(used>cap)throw Error(encrypt?DemoVaultError.NativeUnavailable:DemoVaultError.InvalidEnvelope);
                }
                output.Length=used;
            });
            var drain=GuardPipe(async()=>
            { while(await process.StandardError.BaseStream.ReadAsync(errorDrain.Array,budget.Token).ConfigureAwait(false)!=0) { /* Discard without decode/log. */ } });
            try {await Task.WhenAll(write,read,drain,process.WaitForExitAsync(budget.Token)).ConfigureAwait(false);}
            catch
            {
                Kill();
                // Wait until all pipe tasks have stopped before zeroing their referenced buffers.
                try {await Task.WhenAll(write,read,drain).ConfigureAwait(false);}catch { }
                try {await process.WaitForExitAsync().ConfigureAwait(false);}catch { }
                ct.ThrowIfCancellationRequested();
                if(read.Exception?.Flatten().InnerExceptions.OfType<DemoVaultException>().Any(e=>e.Code==DemoVaultError.InvalidEnvelope)==true)throw Error(DemoVaultError.InvalidEnvelope);
                if(budget.IsCancellationRequested)throw Error(DemoVaultError.NativeUnavailable);
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if(process.ExitCode!=0||output.Length==0)throw Error(DemoVaultError.NativeUnavailable);
            var result=output;output=null;return result;
        }
        catch(DemoVaultException) {throw;}
        catch(OperationCanceledException) {throw Error(DemoVaultError.Cancelled);}
        catch {throw Error(DemoVaultError.NativeUnavailable);}
        finally {output?.Dispose();}
    }
}
