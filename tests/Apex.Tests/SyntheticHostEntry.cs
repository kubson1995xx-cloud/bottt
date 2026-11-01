using System.Threading.Tasks;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Apex.Core.Naval;
using Apex.Desktop.Services;
using Apex.Adapters.Okx.Execution;

namespace Apex.Tests;

// Test executable only; not packaged. The production executable always calls Run(..., false).
// Process-based synthetic trading tests need composition authority, never a JSON/CLI bypass.
internal static class SyntheticHostEntry
{
    public static Task<int> Main(string[] args)
    {
        if(args.Length>0&&args[0]=="--okx-baseline-crash")return OkxOperationalAccountTests.CrashBaseline(args);
        if(args.Length>0&&args[0]=="--okx-d-crash")return DispatchCrashFixture(args);
        if(args.Length>0&&args[0]=="--okx-p-crash")return Task.FromResult(CrashFixture(args));
        return Apex.Desktop.Program.Run(args, trustedSyntheticComposition: true);
    }
    private static async Task<int> DispatchCrashFixture(string[] args)
    {
        if(args.Length!=4||args[3] is not ("dispatch" or "ack"))return 94;
        var f=new OkxExecutionFixture(Path.GetFullPath(args[1]));bool armed=false;OkxExecutionDispatchTests.Peer? peer=null;
        try
        {
            f.Generation=f.Owner.Read().Naval!.Generation+1;f.Owner.Dispose();
            f.Open(new FileStore(f.DirectoryPath,b=>
            {
                if(!armed||b!=(args[3]=="dispatch"?AtomicWriteBoundary.AfterReplace:AtomicWriteBoundary.AfterTempFlush))return;
                var target=System.Text.Json.JsonSerializer.Deserialize<Apex.Core.Engine.EngineState>(File.ReadAllText(f.Target),NavalJson.Options)!;
                if(target.NavalExecution!.Intents.Single().Stage!=NavalExecutionStage.Dispatching)return;
                File.WriteAllText(args[2],args[3]=="dispatch"?"READY Dispatching calls="+peer!.Calls:"READY Ack calls="+peer!.Calls);
                using var timeout=new ManualResetEventSlim(false);timeout.Wait(TimeSpan.FromSeconds(25));throw new IOException("crash fixture owner did not kill child");
            }));
            f.Run();f.Init();var r=f.Owner.Read().Naval!.Reservations.Single();var ids=f.Owner.Read().NavalExecution!.IdAllocations.Single().Ids;
            var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",ids[0].ClientId,f.Clock.Now.AddSeconds(5),new(ids[1].ClientId,"130","90"));
            var intent=f.Intent(new(NavalMutationStatus.Applied,"RESERVED",r.Id,0),wire);
            if(f.Transactions.Prepare(r.Id,intent).Status!=NavalMutationStatus.Applied)return 95;
            using var handler=peer=new OkxExecutionDispatchTests.Peer(f,wire);
            if(args[3]=="ack")peer.AtInvocation=()=>armed=true;else armed=true;
            await using var d=OkxExecutionDispatchTests.Dispatcher(f,peer);await d.DispatchAsync("execution-0",default);return 96;
        }
        catch{return 97;}
        finally{f.Owner.Dispose();} // Never deletes authority. Successful kill cannot execute finally.
    }
    // Local bounded crash fixture only. Never a production command, server, transport or issuer.
    private static int CrashFixture(string[] args)
    {
        if(args.Length!=5||args[3] is not ("AfterTempFlush" or "AfterReplace")||args[4] is not ("id" or "prepare"))return 91;
        var f=new OkxExecutionFixture(Path.GetFullPath(args[1]));bool armed=false;
        try
        {
            f.Generation=f.Owner.Read().Naval!.Generation+1;f.Owner.Dispose();
            f.Open(new FileStore(f.DirectoryPath,b=>
            {
                if(!armed||b.ToString()!=args[3])return;
                File.WriteAllText(args[2],"READY "+b);
                // Owning test kills this process here; timeout bounds a failed parent without deleting authority.
                using var timeout=new ManualResetEventSlim(false);timeout.Wait(TimeSpan.FromSeconds(25));
                throw new IOException("crash fixture owner did not terminate child");
            }));
            f.Run();f.Init();var r=f.Owner.Read().Naval!.Reservations.Single();
            File.WriteAllText(args[2]+".before.json",File.ReadAllText(f.Target));
            if(args[4]=="id")
            {armed=true;f.Transactions.ReserveIds(r.Id,"execution-0",new[]{NavalClientIdRole.Parent});}
            else
            {
                var id=f.Owner.Read().NavalExecution!.IdAllocations.Single().Ids.Single().ClientId;
                var wire=OkxPreparedRequest.Submit(f.Scope,"BTC-USD","buy","fok","4","100",id,f.Clock.Now.AddSeconds(5));
                var intent=f.Intent(new(NavalMutationStatus.Applied,"RESERVED",r.Id,f.Owner.Read().DurableRevision),wire);
                armed=true;f.Transactions.Prepare(r.Id,intent);
            }
            return 92;
        }
        catch{ return 93; }
        finally{f.Owner.Dispose();} // Never deletes evidence/authority. The successful crash does not run finally.
    }
}
