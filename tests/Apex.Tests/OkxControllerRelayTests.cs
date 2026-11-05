using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;
public sealed partial class OkxControllerServerTests
{
    [Fact]
    public async Task Fixed_relay_forwarder_carries_hello_command_and_receipt_without_replay_after_EOF()
    {
        string root=FreshRoot();await using var server=NewServer(root);using var stop=new CancellationTokenSource();var running=server.RunAsync(stop.Token);
        using var input=new FrameInput();using var output=new FrameOutput();
        try
        {
            using var relay=await LinuxControllerRelay.ConnectForTestsAsync(root,default);var forwarding=relay.ForwardAsync(input,output,stop.Token);
            var h=OkxControllerProtocol.DecodeHello(await output.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            var command=Command(h,"SNAPSHOT");input.Feed(OkxControllerProtocol.EncodeCommand(command));
            var r=OkxControllerProtocol.DecodeReceipt(await output.Lines.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(command.CommandId,r.CommandId);Assert.Equal("confirmed",r.Outcome);Assert.False(r.Snapshot!.Connected);
            input.End();Assert.Equal(0,await forwarding.WaitAsync(TimeSpan.FromSeconds(10)));Assert.False(output.Lines.Reader.TryRead(out _));
        }
        finally{stop.Cancel();await running.WaitAsync(TimeSpan.FromSeconds(10));}
        Directory.Delete(root,true);
    }
    private abstract class TestStream:Stream
    {
        public override bool CanRead=>true;public override bool CanWrite=>true;public override bool CanSeek=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override void Flush(){}public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();
    }
    private sealed class FrameInput:TestStream
    {
        private readonly Channel<byte> bytes=Channel.CreateBounded<byte>(4096);
        internal void Feed(string line){foreach(byte b in Encoding.UTF8.GetBytes(line+"\n"))Assert.True(bytes.Writer.TryWrite(b));}
        internal void End()=>bytes.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default){if(!await bytes.Reader.WaitToReadAsync(ct))return 0;byte b=await bytes.Reader.ReadAsync(ct);buffer.Span[0]=b;return 1;}
    }
    private sealed class FrameOutput:TestStream
    {
        internal readonly Channel<string> Lines=Channel.CreateBounded<string>(8);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken ct=default){ct.ThrowIfCancellationRequested();Assert.True(Lines.Writer.TryWrite(Encoding.UTF8.GetString(buffer.Span).TrimEnd('\n')));return ValueTask.CompletedTask;}
    }
}
