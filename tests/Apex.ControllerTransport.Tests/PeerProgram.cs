using System.Text;
using Apex.Desktop.Okx;
using Xunit;
[assembly:CollectionBehavior(DisableTestParallelization=true)]
namespace Apex.ControllerTransport.Tests;
internal static class PeerProgram
{
    public static async Task<int> Main(string[] args)
    {
        if(args.Length!=2||args[0]!="--peer")return 90;
        string root=args[1];
        if(Environment.GetEnvironmentVariable("SSH_ORIGINAL_COMMAND")!=OkxControllerSshLaunchSpec.Relay)return 91;
        using(var process=System.Diagnostics.Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(root,"peer-"+Guid.NewGuid().ToString("N")+".json"),System.Text.Json.JsonSerializer.Serialize(new {Id=process.Id,StartedUtcTicks=process.StartTime.ToUniversalTime().Ticks}));
        File.AppendAllLines(Path.Combine(root,"events"),new[]{"AUTHENTICATED_FIXED_RELAY"});
        string behavior=File.ReadAllText(Path.Combine(root,"behavior"));
        if(behavior=="silent"){await Task.Delay(60000);return 0;}
        if(behavior=="stderr"){await Console.OpenStandardError().WriteAsync(new byte[131072]);await Task.Delay(60000);return 0;}
        if(behavior=="truncated"){await Console.Out.WriteAsync("{\"truncated\":");await Console.Out.FlushAsync();return 0;}
        if(behavior=="oversize"){await Console.Out.WriteLineAsync(new string('x',2000));await Console.Out.FlushAsync();return 0;}
        string epoch=Guid.NewGuid().ToString("N"),session=Guid.NewGuid().ToString("N");
        await using var frames=new OkxControllerClientFrames(Console.OpenStandardInput(),Console.OpenStandardOutput());
        await frames.WriteAsync(OkxControllerProtocol.EncodeHello(new(OkxControllerProtocol.Version,epoch,session)),default);
        while(true)
        {
            string? line=await frames.ReadAsync(1024,default);if(line==null)return 0;
            var c=OkxControllerProtocol.DecodeCommand(line);if(c.Epoch!=epoch||c.Session!=session)return 92;
            File.AppendAllLines(Path.Combine(root,"events"),new[]{c.Action});
            string mode=File.ReadAllText(Path.Combine(root,"state"));
            if(c.Action=="START")
            {
                File.WriteAllText(Path.Combine(root,"state"),mode="RUNNING");
                if(behavior=="lost-start"){await Task.Delay(60000);return 0;}
            }
            if(c.Action=="STOP")File.WriteAllText(Path.Combine(root,"state"),mode="STOP");
            bool armed=mode=="RUNNING";
            var snap=c.Action=="SNAPSHOT"?OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow):null;
            if(snap!=null&&armed)snap=snap with{Connected=true,Mode=mode,Armed=true,Revision=1,Capital=new("EUR",0,0,0,0,null),Observation=new("OPERATIONAL",DateTimeOffset.UtcNow,null,null,null,false,false),GeneratedUtc=DateTimeOffset.UtcNow};
            string id=behavior=="wrong-correlation"&&c.Action=="START"?Guid.NewGuid().ToString("N"):c.CommandId;
            await frames.WriteAsync(OkxControllerProtocol.EncodeReceipt(new(OkxControllerProtocol.Version,epoch,session,id,"confirmed","SYNTHETIC",mode,armed,snap)),default);
        }
    }
}
