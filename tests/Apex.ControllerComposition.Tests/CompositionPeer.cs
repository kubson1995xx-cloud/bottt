using System.Diagnostics;
using System.Text.Json;
using Apex.Desktop.Okx;
using Apex.ControllerTransport.Tests;
namespace Apex.ControllerComposition.Tests;
// Test-only SSH ForceCommand protocol peer, not a second financial executor.
internal static class CompositionPeer
{
    public static async Task<int> Main(string[] args)
    {
        if(args.Length!=2||args[0]!="--peer")return 90;
        string root=args[1];string behavior=File.ReadAllText(Path.Combine(root,"behavior"));
        if(!behavior.StartsWith("composition",StringComparison.Ordinal))return await PeerProgram.Main(args);
        if(Environment.GetEnvironmentVariable("SSH_ORIGINAL_COMMAND")!=OkxControllerSshLaunchSpec.Relay)return 91;
        using(var p=Process.GetCurrentProcess())File.WriteAllText(Path.Combine(root,"peer-"+Guid.NewGuid().ToString("N")+".json"),JsonSerializer.Serialize(new{Id=p.Id,StartedUtcTicks=p.StartTime.ToUniversalTime().Ticks}));
        string epoch=Guid.NewGuid().ToString("N"),session=Guid.NewGuid().ToString("N");
        await using var frames=new OkxControllerClientFrames(Console.OpenStandardInput(),Console.OpenStandardOutput());
        await frames.WriteAsync(OkxControllerProtocol.EncodeHello(new(OkxControllerProtocol.Version,epoch,session)),default);
        bool connected=false,armed=false,prepared=false;long revision=0;
        while(await frames.ReadAsync(1024,default) is {} line)
        {
            var command=OkxControllerProtocol.DecodeCommand(line);File.AppendAllLines(Path.Combine(root,"events"),new[]{command.Action});
            string mode=File.ReadAllText(Path.Combine(root,"state"));
            if(command.Action=="CONNECT")connected=true;
            if(command.Action=="PREPARE")prepared=true;
            if(command.Action=="ARM")armed=true;
            if(command.Action=="START")
            {File.WriteAllText(Path.Combine(root,"state"),mode="RUNNING");if(behavior=="composition-lost-start"){await Task.Delay(60000);return 0;}}
            if(command.Action is "STOP" or "EMERGENCY"){File.WriteAllText(Path.Combine(root,"state"),mode="STOP");armed=false;}
            var now=DateTimeOffset.UtcNow;
            OkxControllerSnapshot? snapshot=null;
            if(command.Action=="SNAPSHOT")
            {
                snapshot=OkxControllerSnapshotCodec.Disconnected(now);
                if(connected)snapshot=snapshot with{Connected=true,Mode=mode,Armed=armed,Ready=prepared&&armed,Revision=++revision,Capital=new("EUR",100m,2m,98m,0m,null),Observation=new("OPERATIONAL",now,now,now,now,true,true),Balances=new(2,false,new[]{new OkxControllerBalance("EUR",100m,98m,2m,now,false),new OkxControllerBalance("BTC",1m,1m,0m,now,true)}),Catalog=new(1,false,new[]{new OkxControllerInstrument("SOL-EUR","SOL","EUR",0.01m,0.1m,0.01m)}),History=new(1,false,new[]{new OkxControllerFill("SOL-EUR",false,1m,10m,0.01m,"EUR",now)})};
            }
            await frames.WriteAsync(OkxControllerProtocol.EncodeReceipt(new(OkxControllerProtocol.Version,epoch,session,command.CommandId,"confirmed",command.Action=="PREPARE"?"DEMO_PREPARED":"SYNTHETIC",mode,armed,snapshot)),default);
        }
        return 0;
    }
}
