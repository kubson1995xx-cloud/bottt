using System.Text;
using System.Threading.Channels;
using Apex.Adapters.Okx.Execution;
using Apex.Desktop.Okx;
namespace Apex.Okx.DemoAcceptance;

// A bridge to the *existing* host action lane, not another execution engine.
internal sealed partial class LinuxDemoHost
{
    private LinuxControllerAdmission.LocalControllerCapability? controllerCapability;
    private long controllerCommandEpoch;
    internal sealed class ControllerAttachment:IAsyncDisposable
    {
        private readonly LinuxDemoHost host;
        private readonly Channel<string> input;
        private readonly ReceiptOutput output;
        private readonly Task<int> running;
        internal ControllerAttachment(LinuxControllerAdmission.LocalControllerCapability capability,string root,LinuxDemoCredentialVault vault,
            Func<EphemeralCredentials,CancellationToken,Task<OkxEnrollmentDiscovery>> discover,FiniteEntryTestComposition? transport,bool synthetic,CancellationToken ct)
        {
            if(!capability.Current)throw new IOException("CONTROLLER_CAPABILITY_REFUSED");
            input=Channel.CreateBounded<string>(1);output=new();
            host=new(root,vault,async c=>await input.Reader.ReadAsync(c),output,discover,transport,()=>capability.Current&&(synthetic||LinuxInput.SafeForPrivateDispatch()),ct){controllerCapability=capability};
            host.advisory=instrument=>synthetic?OkxAdvisoryComparison.Unavailable("NOT_CONFIGURED") with{Instrument=instrument}:OkxAdvisoryReader.ReadProductionForInstrument(instrument,DateTimeOffset.UtcNow);
            running=host.RunCoreAsync();
        }
        internal (string Mode,bool Armed) State(){var s=host.loop?.ReadStatus();return(s?.Mode??"STOP",s?.Armed??false);}
        internal OkxControllerSnapshot Snapshot()=>host.loop?.ReadControllerSnapshot()??OkxControllerSnapshotCodec.Disconnected(DateTimeOffset.UtcNow);
        internal void Revoke()=>host.Revoke();
        internal void ControllerLost(bool confirmedRunning)
        {lock(host.gate){if(!confirmedRunning)host.Revoke();}}
        internal async Task<(string Outcome,string Code,OkxControllerSnapshot? Snapshot)> Execute(string command)
        {
            if(!OkxControllerProtocol.Actions.Contains(command))throw new IOException("CONTROLLER_ACTION_REFUSED");
            var completion=output.Begin();Interlocked.Exchange(ref host.controllerCommandEpoch,Interlocked.Read(ref host.epoch));
            if(!input.Writer.TryWrite(command))throw new IOException("HOST_LANE_BUSY");
            string result=await completion.WaitAsync(host.lifetime.Token);
            var state=State();OkxControllerSnapshot? snapshot=command=="SNAPSHOT"&&result.StartsWith("CONTROLLER_SNAPSHOT ",StringComparison.Ordinal)?OkxControllerSnapshotCodec.Decode(result):null;
            bool connected=host.account?.IsConnected==true;bool confirmed=command switch
            {
                "CONNECT"=>result=="CONNECTED_STOP"&&connected,
                "PREPARE"=>result=="ORDINARY_DEMO_PREPARED",
                "ARM"=>result=="ARMED_STOP"&&state.Armed,
                "START"=>result=="SCHEDULER_STARTED"&&state.Mode=="RUNNING",
                "STOP" or "EMERGENCY"=>result=="STOP_DRAINED_UNARMED"&&!state.Armed&&state.Mode=="STOP",
                "DISCONNECT"=>result=="STOP_DISCONNECTED_UNARMED"&&!connected,
                "SNAPSHOT"=>result.StartsWith("CONTROLLER_SNAPSHOT ",StringComparison.Ordinal),
                "STATUS"=>result.StartsWith("STATUS ",StringComparison.Ordinal)||result=="STOP_DISCONNECTED_UNARMED",
                _=>result.StartsWith("ADVISORY_STATUS ",StringComparison.Ordinal)
            };
            // No arbitrary old-host text, private operands or provider strings cross.
            string code=confirmed?"ACTION_CONFIRMED":"HOST_ACTION_REFUSED";
            if(confirmed&&command.StartsWith("ADVISORY-STATUS ",StringComparison.Ordinal))
            {
                var advisory=System.Text.Json.JsonSerializer.Deserialize<OkxAdvisoryStatus>(result["ADVISORY_STATUS ".Length..]);
                code=advisory?.Comparison switch{"AGREE_BUY"=>"ADVISORY_AGREE_BUY_SHADOW_ONLY","AGREE_SELL"=>"ADVISORY_AGREE_SELL_SHADOW_ONLY","AGREE_WAIT"=>"ADVISORY_AGREE_WAIT_SHADOW_ONLY","DISAGREE"=>"ADVISORY_DISAGREE_SHADOW_ONLY",_=>"ADVISORY_UNAVAILABLE_SHADOW_ONLY"};
            }
            return(confirmed?"confirmed":"refused",code,snapshot);
        }
        public async ValueTask DisposeAsync()
        {host.Revoke();host.lifetime.Cancel();await running.ConfigureAwait(false);}
        private sealed class ReceiptOutput:TextWriter
        {
            public override Encoding Encoding=>Encoding.UTF8;
            private TaskCompletionSource<string>? pending;
            internal Task<string> Begin(){var t=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);if(Interlocked.CompareExchange(ref pending,t,null)!=null)throw new IOException("HOST_LANE_BUSY");return t.Task;}
            public override void WriteLine(string? value){Interlocked.Exchange(ref pending,null)?.TrySetResult(value??"");}
        }
    }
}
