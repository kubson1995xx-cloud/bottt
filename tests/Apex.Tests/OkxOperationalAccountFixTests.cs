using System;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Engine;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;

namespace Apex.Tests;

public sealed class OkxOperationalAccountFixTests
{
    // Catches treating the currency's last change as the signed response's capture time.
    [Fact]
    public async Task Idle_balance_last_changed_two_days_ago_connects_and_preserves_original_provenance()
    {
        await using var f=new OperationalPeer();
        var originalUtc=DateTimeOffset.FromUnixTimeMilliseconds(f.Clock.GetUtcNow().AddDays(-2).ToUnixTimeMilliseconds());
        int balanceReads=0;
        f.Mutate=(path,body)=>
        {
            if(path!="/api/v5/account/balance")return;
            balanceReads++;
            body["data"]![0]!["details"]![0]!["uTime"]=originalUtc.ToUnixTimeMilliseconds().ToString();
        };
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);
        Assert.NotNull(lease);
        bool connected=await f.Service.ConnectAsync(f.Request,lease!,default);
        Assert.Equal(2,balanceReads); // Identical last-change time at both fresh bookends.
        Assert.True(connected,f.Service.Status);
        Assert.True(await f.Service.ApproveAndCreateAsync(default));
        Assert.Equal(3,balanceReads); // Same operand in the post-approval signed recheck.
        var baseline=System.Text.Json.JsonSerializer.Deserialize<EngineState>(File.ReadAllText(f.Target))!.NavalBroker!.Baseline;
        Assert.Equal(originalUtc,baseline.Observations.Balances.Single().SourceUtc);
        Assert.Equal(1234.56m,baseline.InitialCash);
        Assert.Equal("STOP",f.Service.Coordinator!.Snapshot().Mode);
        Assert.Equal(NavalBrokerValidation.Digest(baseline.Binding,baseline.Observations,baseline.KeyScopeDigest),baseline.Digest);
        var changed=baseline.Observations with{Balances=new[]{baseline.Observations.Balances.Single() with{SourceUtc=originalUtc.AddMilliseconds(1)}}};
        Assert.NotEqual(baseline.Digest,NavalBrokerValidation.Digest(baseline.Binding,changed,baseline.KeyScopeDigest));
        Assert.True(baseline.Observations.CaptureUtc>originalUtc.AddDays(1));
        Assert.All(baseline.Observations.Surfaces,s=>
        {
            Assert.True(s.RequestUtc>=baseline.Observations.CaptureUtc);
            Assert.True(s.ReceiveUtc>=s.RequestUtc);
            Assert.True(s.RequestTimestamp>=baseline.Observations.CaptureTimestamp);
            Assert.True(s.ReceiveTimestamp>=s.RequestTimestamp);
        });
    }

    // Catches the ordinary InspectAsync false-return branch leaving its private session live.
    [Theory]
    [InlineData("null")]
    [InlineData("policy")]
    [InlineData("ip")]
    public async Task Refused_permission_inspection_drains_both_sockets_and_allows_fresh_other_service(string refusal)
        =>await OkxSyntheticClockIsolationTests.Run("inspection-"+refusal);
    internal static async Task InspectionScenario(string refusal)
    {
        await using var f=new OperationalPeer();
        var provider=new InspectionProvider(refusal);
        var fence=new CapturingFence();
        await using var service=Service(f,provider,fence);
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);
        Assert.NotNull(lease);
        Assert.False(await service.ConnectAsync(f.Request,lease!,default));
        Assert.Equal(1,provider.Inspections);
        Assert.Equal("PERMISSION_SCOPE_UNPROVEN",service.Status);
        Assert.Null(service.Coordinator);Assert.False(File.Exists(f.Target));Assert.Equal(0,f.Approvals);
        Assert.NotNull(fence.Context);Assert.NotNull(fence.Scope);
        Assert.Equal((false,false,2,2),(service.IsConnected,service.CanObserve(fence.Scope!,fence.Context!,f.Clock.GetUtcNow()),f.Sockets.Count,f.Sockets.Count(s=>s.State==WebSocketState.Closed)));
        // A different service under the SAME runtime, with a newly authorized lease.
        await f.Connect();
        Assert.True(f.Service.IsConnected);Assert.Equal("BASELINE_APPROVAL_REQUIRED",f.Service.Status);
        Assert.Equal(4,f.Sockets.Count);
        Assert.All(f.Sockets.Take(2),s=>Assert.Equal(WebSocketState.Closed,s.State));
        Assert.All(f.Sockets.Skip(2),s=>Assert.Equal(WebSocketState.Open,s.State));
        await f.Service.DisconnectAsync();
        Assert.All(f.Sockets,s=>Assert.Equal(WebSocketState.Closed,s.State));
    }

    [Theory]
    [InlineData("future")]
    [InlineData("moved")]
    public async Task Last_change_future_date_or_changed_balance_bookend_still_refuses(string mutation)
    {
        await using var f=new OperationalPeer();int reads=0;
        string future=f.Clock.GetUtcNow().AddDays(2).ToUnixTimeMilliseconds().ToString();
        f.Mutate=(path,body)=>
        {
            if(path!="/api/v5/account/balance")return;
            reads++;
            if(mutation=="future"||reads==2)body["data"]![0]!["details"]![0]!["uTime"]=future;
        };
        var lease=await f.Runtime.Activation.RequestAsync(f.Request,default);
        Assert.False(await f.Service.ConnectAsync(f.Request,lease!,default));
        Assert.Equal(2,reads);Assert.False(f.Service.IsConnected);Assert.False(File.Exists(f.Target));
        Assert.Equal(2,f.Sockets.Count);Assert.All(f.Sockets,s=>Assert.Equal(WebSocketState.Closed,s.State));
    }

    private static OkxAccountConnectionService Service(OperationalPeer f,IOkxProtectedBaselineProvider provider,CapturingFence capture)
        =>OkxAccountConnectionService.ForTests(f.Runtime,f.Root,f.Binding,provider,f.Clock,
            (scope,secrets,fp,fence)=>
            {
                capture.Inner=fence;
                return OkxExecutionObservationClient.ForTests(f,f.Clock,scope,secrets,fp,capture,(t,ct)=>{f.Clock.Advance(t);return Task.CompletedTask;});
            },
            (scope,secrets,fp,fence)=>OkxObservationStream.ForTests((uri,ct)=>
            {
                var socket=new OperationalSocket(uri);f.Sockets.Add(socket);return Task.FromResult<WebSocket>(socket);
            },f.Clock,scope,secrets,fp,fence));
    private sealed class CapturingFence : IOkxObservationReadFence
    {
        internal IOkxObservationReadFence Inner=null!;
        internal OkxExecutionScope? Scope;
        internal OkxObservationContext? Context;
        public bool CanObserve(OkxExecutionScope scope,OkxObservationContext context,DateTimeOffset utc)
        {Scope=scope;Context=context;return Inner.CanObserve(scope,context,utc);}
    }
    private sealed class InspectionProvider(string refusal) : IOkxProtectedBaselineProvider
    {
        internal int Inspections;
        public ValueTask<OkxKeyScopeInspection?> InspectAsync(OkxScopeBinding binding,string fingerprint,CancellationToken ct)
        {
            Inspections++;
            return ValueTask.FromResult<OkxKeyScopeInspection?>(refusal=="null"?null:new(fingerprint,refusal=="policy"?"wrong-policy":"okx-dedicated-cash-no-funding-v1","synthetic-inspection",refusal=="ip"?"192.0.2.2":"192.0.2.1"));
        }
        public ValueTask<bool> ApproveAsync(NavalBaselineApprovalRequest request,CancellationToken ct)=>throw new Exception("Refused inspection reached approval");
    }
}
