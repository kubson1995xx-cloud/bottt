using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Apex.Desktop.Okx;
using Apex.Okx.DemoAcceptance;
using Xunit;

namespace Apex.Tests;

public sealed class OkxAdvisoryHostAuthorityTests
{
    [Fact]
    public async Task Armed_STOP_refuses_advisory_without_revoking_arming_or_touching_synthetic_owner()
    {
        if(!OperatingSystem.IsLinux())return;
        await using var fixture=new OperationalPeer();await fixture.Connect();Assert.True(await fixture.Service.ApproveAndCreateAsync(default));
        using var peer=new TradingPeer(fixture);
        await using var loop=OkxOperationalTradingService.ForTests(fixture.Service,
            (scope,secrets,fingerprint,fence)=>Apex.Adapters.Okx.Execution.OkxExecutionClient.ForTests(peer,fixture.Clock,scope,secrets,fingerprint,fence),peer,fixture.Clock);
        var request=fixture.Request with{Purpose=OkxActivationPurpose.Execute,Session=fixture.Service.TradingSessionId};
        var lease=await fixture.Runtime.Activation.RequestAsync(request,default);Assert.True(loop.Arm(request,lease!));
        Assert.True(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);
        var input=Channel.CreateUnbounded<string>();using var output=new Lines();var type=typeof(LinuxDemoHost);
        var host=type.GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single().Invoke(new object?[]{fixture.Root,null,
            (Func<CancellationToken,ValueTask<string?>>)(async ct=>await input.Reader.ReadAsync(ct)),output,null,null,(Func<bool>)(()=>true),CancellationToken.None});
        type.GetField("loop",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,loop);
        type.GetField("account",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,fixture.Service);
        type.GetField("advisory",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(host,(Func<string,OkxAdvisoryStatus>)(_=>throw new Exception("ARMED_REPORT_READ_FORBIDDEN")));
        var run=(Task<int>)type.GetMethod("RunCoreAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(host,null)!;
        try
        {
            await output.Until("LOCAL DEMO:");byte[] before=File.ReadAllBytes(fixture.Target);int reads=fixture.Paths.Count,opens=fixture.SecretOpens;
            input.Writer.TryWrite("ADVISORY-STATUS");await output.Until("ADVISORY_REFUSED_STOP_UNARMED_REQUIRED");
            Assert.True(loop.ReadStatus().Armed);Assert.Equal("STOP",loop.ReadStatus().Mode);Assert.Equal(before,File.ReadAllBytes(fixture.Target));
            Assert.Equal(reads,fixture.Paths.Count);Assert.Equal(opens,fixture.SecretOpens);Assert.Equal(0,peer.Posts);
        }
        finally{input.Writer.TryWrite("QUIT");await run.WaitAsync(TimeSpan.FromSeconds(10));}
        Assert.Equal(0,await run);
    }
    private sealed class Lines:TextWriter
    {
        private readonly Channel<string> lines=Channel.CreateUnbounded<string>();
        public override Encoding Encoding=>Encoding.UTF8;
        public override void WriteLine(string? value)=>lines.Writer.TryWrite(value??"");
        internal async Task Until(string prefix)
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while(!(await lines.Reader.ReadAsync(timeout.Token)).StartsWith(prefix,StringComparison.Ordinal)){}
        }
    }
}
