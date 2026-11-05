using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
namespace Apex.Desktop.Okx;

internal sealed record OkxRemoteCommandResult(string Outcome,string Code,string? ConfirmedMode=null);
internal sealed record OkxRemoteControllerStatus(string Availability,bool Configured,bool ChannelConnected,bool Stale,
    bool CanConnectAccount,bool CanPrepare,bool CanArm,bool CanStart,string? Mode,bool? Armed,int Pending,string Outcome,string Code,
    string? LastConfirmedControl,OkxControllerSnapshot? Snapshot);

// One remote executor, no broker credentials or local execution. Only native composition may supply a transport.
internal sealed class OkxRemoteControllerControl:IAsyncDisposable
{
    private readonly Func<CancellationToken,Task<IOkxControllerFrameTransport>>? transportFactory;
    private readonly Func<OkxControllerClient> clientFactory;
    private readonly object gate=new();
    private readonly SemaphoreSlim lifecycle=new(1,1);
    private readonly Dictionary<string,(string Action,Task<OkxRemoteCommandResult> Result)> commands=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource life=new();
    private OkxControllerClient? client;
    private OkxControllerClientView? retained;
    private CancellationTokenSource? polling;
    private Task pollTask=Task.CompletedTask;
    private bool disposed,connecting;
    private string? lastConfirmed;
    internal OkxRemoteControllerControl():this(null){}
    internal OkxRemoteControllerControl(Func<CancellationToken,Task<IOkxControllerFrameTransport>>? transportFactory,Func<OkxControllerClient>? clientFactory=null)
    {this.transportFactory=transportFactory;this.clientFactory=clientFactory??(()=>new(TimeSpan.FromSeconds(8),TimeSpan.FromSeconds(15)));}
    internal OkxRemoteControllerStatus ReadStatus()
    {
        lock(gate)
        {
            var v=client?.View??retained;bool fresh=!disposed&&!connecting&&v?.TransportFresh==true;
            bool stale=!fresh||v?.SnapshotStale!=false;var s=v?.Snapshot;
            bool admitted=fresh&&!stale&&s?.Connected==true&&v?.Outcome=="confirmed"&&v.Pending==0;
            bool canConnect=fresh&&!stale&&s?.Connected==false&&v?.Outcome=="confirmed"&&v.Pending==0;
            return new(transportFactory==null?"NOT_CONFIGURED":connecting?"CONNECTING":!fresh?"DISCONNECTED":stale?"STALE":"CONNECTED",
                transportFactory!=null,fresh,stale,canConnect,admitted,admitted&&s?.Mode=="STOP",admitted&&s?.Ready==true&&s.Armed&&!s.Emergency&&s.Mode=="STOP",
                v?.Mode,v?.Armed,v?.Pending??0,v?.Outcome??"pending-or-unknown",v?.Code??(transportFactory==null?"NOT_CONFIGURED":"NOT_CONNECTED"),lastConfirmed,s);
        }
    }
    internal Task<OkxRemoteCommandResult> ExecuteAsync(string action,string id)
    {
        lock(gate)
        {
            if(commands.TryGetValue(id,out var old))return old.Action==action?old.Result:Task.FromResult(new OkxRemoteCommandResult("refused","COMMAND_ID_CONFLICT"));
            if(disposed)return Task.FromResult(new OkxRemoteCommandResult("refused","CONTROLLER_CLOSED"));
            // Never evict uncertain mutation identities or retry a mutation, even after reconnect.
            if(commands.Count>=(action is "stop" or "emergency"?256:240))return Task.FromResult(new OkxRemoteCommandResult("refused","LOCAL_COMMAND_BUDGET_EXHAUSTED"));
            var done=new TaskCompletionSource<OkxRemoteCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            commands.Add(id,(action,done.Task));_=Task.Run(()=>Complete(action,done));return done.Task;
        }
    }
    private async Task Complete(string action,TaskCompletionSource<OkxRemoteCommandResult> done)
    {
        try{done.TrySetResult(await Perform(action));}
        catch(Exception e) when(e is not OutOfMemoryException){done.TrySetResult(new("pending-or-unknown","CONTROLLER_UNAVAILABLE_QUERY_STATE"));}
    }
    private async Task<OkxRemoteCommandResult> Perform(string action)
    {
        if(action is "connect" or "disconnect")
        {
            await lifecycle.WaitAsync(life.Token);
            try
            {
                if(action=="disconnect"){await Close();return new("confirmed","CHANNEL_CLOSED_VPS_MAY_CONTINUE");}
                if(transportFactory==null)return new("refused","NOT_CONFIGURED");
                await Close();lock(gate){if(disposed)return new("refused","CONTROLLER_CLOSED");connecting=true;client=clientFactory();}
                try
                {
                    using var budget=CancellationTokenSource.CreateLinkedTokenSource(life.Token);budget.CancelAfter(TimeSpan.FromSeconds(15));
                    var transport=await transportFactory(budget.Token);
                    await client.ReconnectAsync(transport,budget.Token);
                    lock(gate){polling=CancellationTokenSource.CreateLinkedTokenSource(life.Token);pollTask=Poll(client,polling.Token);}
                    return new("confirmed","CHANNEL_CONNECTED_STATE_OBSERVED");
                }
                catch(Exception e) when(e is not OutOfMemoryException){await Close();return new("refused","CONNECTION_UNAVAILABLE");}
                finally{lock(gate)connecting=false;}
            }
            finally{lifecycle.Release();}
        }
        OkxControllerClient? current;lock(gate)current=client;
        if(current==null)return new("refused",transportFactory==null?"NOT_CONFIGURED":"MANUAL_RECONNECT_REQUIRED");
        var status=ReadStatus();
        if(action=="connect-account"&&!status.CanConnectAccount||action=="start"&&!status.CanStart||action=="prepare"&&!status.CanPrepare||action=="arm"&&!status.CanArm)return new("refused","FRESH_READINESS_REQUIRED");
        string wire=action switch{"connect-account"=>"CONNECT","prepare"=>"PREPARE","arm"=>"ARM","start"=>"START","stop"=>"STOP","emergency"=>"EMERGENCY","refresh"=>"SNAPSHOT",_=>""};
        if(wire.Length==0)return new("refused","COMMAND_REFUSED");
        // No controller operation lock: STOP/EMERGENCY use the client's urgent queue during pending reads.
        var r=await current.CommandAsync(wire,life.Token);
        string? confirmed=r.Outcome=="confirmed"?r.Receipt?.Mode:null;
        if(r.Outcome=="confirmed"&&action is "start" or "stop" or "emergency")lock(gate)lastConfirmed=wire+":"+confirmed;
        return new(r.Outcome,r.Code,confirmed);
    }
    private static async Task Poll(OkxControllerClient client,CancellationToken token)
    {
        try{await Task.Delay(TimeSpan.FromSeconds(2),token);await client.PollAsync(TimeSpan.FromSeconds(2),token);}
        catch(OperationCanceledException){}
    }
    private async Task Close()
    {
        OkxControllerClient? old;CancellationTokenSource? cancel;Task task;
        lock(gate){old=client;client=null;cancel=polling;polling=null;task=pollTask;if(old!=null)retained=old.View with{SnapshotStale=true,TransportFresh=false};}
        cancel?.Cancel();if(old!=null)await old.DisposeAsync();await task;cancel?.Dispose();
    }
    public async ValueTask DisposeAsync()
    {
        lock(gate){if(disposed)return;disposed=true;life.Cancel();}
        await lifecycle.WaitAsync();try{await Close();}finally{lifecycle.Release();}
    }
}
