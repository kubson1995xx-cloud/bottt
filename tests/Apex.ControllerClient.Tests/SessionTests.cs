using Apex.Desktop.Okx;
using System.IO.Pipelines;
using Xunit;
namespace Apex.ControllerClient.Tests;
public sealed class SessionTests
{
    internal sealed class Peer:IAsyncDisposable
    {
        readonly Pipe up=new(),down=new();
        internal OkxControllerClientFrames Client {get;}
        internal OkxControllerClientFrames Server {get;}
        internal string Epoch=Guid.NewGuid().ToString("N"),Session=Guid.NewGuid().ToString("N");
        internal Peer(){Client=new(down.Reader.AsStream(),up.Writer.AsStream());Server=new(up.Reader.AsStream(),down.Writer.AsStream());}
        internal Task Hello()=>Server.WriteAsync(OkxControllerProtocol.EncodeHello(new(OkxControllerProtocol.Version,Epoch,Session)),default);
        internal async Task<OkxControllerCommand> Read()=>OkxControllerProtocol.DecodeCommand(await Server.ReadAsync(1024,default)??throw new IOException("EOF"));
        internal Task Reply(OkxControllerCommand c,string mode="STOP",string outcome="confirmed",OkxControllerSnapshot? snapshot=null)=>Server.WriteAsync(OkxControllerProtocol.EncodeReceipt(new(OkxControllerProtocol.Version,Epoch,Session,c.CommandId,outcome,"SYNTHETIC",mode,false,snapshot)),default);
        internal async Task Bootstrap(){await Hello();var status=await Read();Assert.Equal("STATUS",status.Action);await Reply(status);var snap=await Read();Assert.Equal("SNAPSHOT",snap.Action);await Reply(snap,snapshot:OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow));}
        public async ValueTask DisposeAsync(){await Client.DisposeAsync();await Server.DisposeAsync();}
    }
    [Fact] public async Task Manual_session_queries_authoritative_state_and_preserves_snapshot_clocks()
    {
        await using var peer=new Peer();await using var client=new OkxControllerClient(TimeSpan.FromSeconds(2),TimeSpan.FromSeconds(5));
        var server=peer.Bootstrap();await client.ReconnectAsync(peer.Client);await server;
        Assert.Equal("confirmed",client.View.Outcome);Assert.False(client.View.SnapshotStale);Assert.NotNull(client.View.Snapshot);
        var generated=client.View.Snapshot!.GeneratedUtc;
        var pending=client.CommandAsync("STATUS");var command=await peer.Read();await peer.Reply(command,"RUNNING");await pending;
        Assert.Equal(generated,client.View.Snapshot.GeneratedUtc);Assert.Equal("RUNNING",client.View.Mode);
    }
}
