using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;

// Owns one explicit admitted OS SSH process. Never reconnects, retries commands,
// changes StartInfo after admission, or sends STOP/DISCONNECT during local close.
internal sealed class WindowsOkxControllerSshTransport:IOkxControllerFrameTransport
{
    private readonly object gate=new();
    private readonly WindowsOkxControllerTransportAdmission admission;
    private readonly Process child;
    private readonly OkxControllerClientFrames frames;
    private readonly CancellationTokenSource life=new();
    private readonly Task stderr;
    private Task? drain;
    private long stderrBytes;
    private int reaped;
    internal bool ChildReaped=>Volatile.Read(ref reaped)!=0;
    internal long StderrBytes=>Interlocked.Read(ref stderrBytes);
    private WindowsOkxControllerSshTransport(WindowsOkxControllerTransportAdmission admission,Process child)
    {
        this.admission=admission;this.child=child;
        frames=new(child.StandardOutput.BaseStream,child.StandardInput.BaseStream);
        stderr=DrainError();
    }
    internal static WindowsOkxControllerSshTransport Start(WindowsOkxControllerTransportAdmission admission,CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(admission);
        // Keep the exact lease locked through the actual OS process creation boundary.
        return admission.WithValidatedSpec(admission.Spec,()=>
        {
            cancellation.ThrowIfCancellationRequested();
            var child=new Process{StartInfo=admission.Spec.CreateStartInfo(admission.Capability)};
            try
            {
                cancellation.ThrowIfCancellationRequested();
                if(!child.Start())throw new IOException("CONTROLLER_SSH_START_REFUSED");
                return new WindowsOkxControllerSshTransport(admission,child);
            }
            catch{child.Dispose();throw;}
        });
    }
    private void Terminate()
    {
        life.Cancel();
        try{if(!child.HasExited)child.Kill(true);}
        catch(InvalidOperationException){} // Already exited; exact retained handle is reaped below.
    }
    private async Task DrainError()
    {
        byte[] buffer=new byte[4096];
        try
        {
            while(true)
            {
                int n=await child.StandardError.BaseStream.ReadAsync(buffer,life.Token);
                if(n==0)return;
                long total=Interlocked.Read(ref stderrBytes);
                Interlocked.Exchange(ref stderrBytes,Math.Min(65536,total+n));
                if(total+n>65536){Terminate();return;}
            }
        }
        catch(Exception e) when(e is IOException or ObjectDisposedException or OperationCanceledException){ }
        // No stderr content, exception text or private paths leave this transport.
    }
    public async Task<string?> ReadAsync(int cap,CancellationToken cancellation)
    {
        if(cap<1||cap>OkxControllerProtocol.ReceiptLimit)throw new ArgumentOutOfRangeException(nameof(cap));
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation,life.Token);budget.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            string? line=await frames.ReadAsync(cap,budget.Token);
            if(line==null)await Close();
            return line;
        }
        catch(Exception e) when(e is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or DecoderFallbackException)
        {
            await Close();
            if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);
            throw new IOException("CONTROLLER_SSH_READ_REFUSED");
        }
    }
    public async Task WriteAsync(string line,CancellationToken cancellation)
    {
        if(line==null||line.IndexOfAny(new[]{'\r','\n'})>=0||Encoding.UTF8.GetByteCount(line)>OkxControllerProtocol.CommandLimit)throw new IOException("CONTROLLER_SSH_FRAME_REFUSED");
        using var budget=CancellationTokenSource.CreateLinkedTokenSource(cancellation,life.Token);budget.CancelAfter(TimeSpan.FromSeconds(10));
        try{await frames.WriteAsync(line,budget.Token);}
        catch(Exception e) when(e is IOException or InvalidDataException or ObjectDisposedException or OperationCanceledException or EncoderFallbackException)
        {
            await Close();
            if(cancellation.IsCancellationRequested)throw new OperationCanceledException(cancellation);
            throw new IOException("CONTROLLER_SSH_WRITE_REFUSED");
        }
    }
    private Task Close(){lock(gate)return drain??=CloseCore();}
    private async Task CloseCore()
    {
        // All callers share this one drain. Kill/reap releases blocked pipes, but never
        // emits a remote command or assumes the authoritative engine is stopped.
        Terminate();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Volatile.Write(ref reaped,1);
        try{await stderr.WaitAsync(TimeSpan.FromSeconds(5));await frames.DisposeAsync();}
        finally{child.Dispose();admission.Dispose();}
    }
    public ValueTask DisposeAsync()=>new(Close());
}
