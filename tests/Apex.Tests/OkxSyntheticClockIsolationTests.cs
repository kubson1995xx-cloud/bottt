using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Tests;
public sealed class OkxSyntheticClockIsolationTests
{
    [Fact]
    public async Task Future_financial_fixture_cannot_poison_next_WebSocket_connection_quota_clock()
    {
        await Run("future");
        // Observe only nonsecret original scheduling operands. Do not reset the production
        // quota registry, shorten its wait, retry a connection or relax a deadline.
        var next=(DateTimeOffset)typeof(OkxObservationStream).GetField("nextConnect",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
        await using var fresh=new OperationalPeer();var lease=await fresh.Runtime.Activation.RequestAsync(fresh.Request,default);
        bool connected=await fresh.Service.ConnectAsync(fresh.Request,lease!,default);
        Assert.True(connected,"status="+fresh.Service.Status+" publicQuotaAheadSeconds="+(next-DateTimeOffset.UtcNow).TotalSeconds+" privateReads="+fresh.Paths.Count+" sockets="+fresh.Sockets.Count);
        Assert.Equal(2,fresh.Sockets.Count);Assert.Contains(fresh.Paths,p=>p=="/api/v5/account/balance");
    }
    internal static async Task<int> Scenario(string scenario)
    {
        if(scenario=="future")
        {await using var future=new OperationalPeer();future.Clock.Advance(TimeSpan.FromSeconds(31));await future.Connect();return 0;}
        if(scenario.StartsWith("inspection-",StringComparison.Ordinal))
        {await OkxOperationalAccountFixTests.InspectionScenario(scenario[11..]);return 0;}
        return 98;
    }
    internal static async Task Run(string scenario)
    {
        // Each fixture that reconnects after artificially advancing UTC owns its process.
        // Neither WS nor HTTP process-lifetime production quota state is reset or bypassed.
        var start=new System.Diagnostics.ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        start.ArgumentList.Add(typeof(SyntheticHostEntry).Assembly.Location);start.ArgumentList.Add("--okx-clock-isolation");start.ArgumentList.Add(scenario);
        using var child=System.Diagnostics.Process.Start(start)!;
        var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
        try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));await Task.WhenAll(stdout,stderr);Assert.Equal(0,child.ExitCode);}
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }
}
