using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;
namespace Apex.Tests;
public sealed class OkxMixedNativeCycleTests
{
    [Theory]
    [InlineData("mixed-cash-debt")][InlineData("mixed-cash-raw-debt")][InlineData("mixed-cash-excluded")][InlineData("mixed-cash-child")][InlineData("mixed-cash-stale")]
    public Task Own_cash_context_retains_debt_movement_children_and_valuation_vetoes(string scenario)=>RefusalScenario(scenario);
    [Theory]
    [InlineData("mixed-debt")][InlineData("mixed-raw-debt")][InlineData("mixed-excluded")][InlineData("mixed-child")][InlineData("mixed-stale")]
    public Task Current_debt_movement_unknown_children_or_stale_valuation_cannot_transition(string scenario)=>RefusalScenario(scenario);
    private static async Task RefusalScenario(string scenario)
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,scenario))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","CONNECTED_STOP");await child.Command("FAULT","STATUS mode=");
            await child.Command("PREPARE","ORDINARY_DEMO_FRESH_RECONCILIATION_REQUIRED");await child.Command("ARM","ARMED_STOP");await child.Command("START","START_REFUSED");
            await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("MIXED_NATIVE_PASS",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public Task Own_cash_context_new_dust_still_stops_without_continuation()=>DustScenario(true);
    [Fact]
    public Task Newly_created_untradeable_fee_dust_is_not_the_historical_pin_and_stops_without_continuation()=>DustScenario(false);
    private static async Task DustScenario(bool cash)
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,cash?"mixed-cash-newdust":"mixed-newdust"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","CONNECTED_STOP");await child.Command("PREPARE","ORDINARY_DEMO_PREPARED");await child.Command("ARM","ARMED_STOP");await child.Command("START","SCHEDULER_STARTED");
            for(int n=0;n<80;n++){await child.Send("STATUS");var line=await child.Expect("STATUS mode=");if(line.Contains("mode=STOP")&&line.Contains("fills=3")&&line.Contains("obligations=2"))break;Assert.True(n<79,child.Log);await Task.Delay(200);}
            await child.Command("START","START_REFUSED");await child.Command("STOP","STOP_DRAINED_UNARMED");await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("posts=1",child.Log);
        }
        Directory.Delete(root,true);
    }
    [Fact]
    public Task Six_nullable_SOL_only_same_owner_nullACK_dust_autonomous_two_signals_and_restart_STOP()=>CycleScenario(false);
    [Fact]
    public Task Own_cash_fields_native_two_autonomous_cycles_and_restart_STOP()=>CycleScenario(true);
    private static async Task CycleScenario(bool cash)
    {
        if(!OperatingSystem.IsLinux())return;
        string root="/home/ubuntu/apex-deliveries/linux-host-native-"+Guid.NewGuid().ToString("N");
        using(var child=HostChild.Start(root,cash?"mixed-cash-cycle":"mixed-cycle"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","CONNECTED_STOP");await child.Command("START","START_REFUSED");
            await child.Command("PREPARE","ORDINARY_DEMO_PREPARED");await child.Command("PREPARE","ORDINARY_DEMO_ALREADY_PREPARED");await child.Command("START","START_REFUSED");
            await child.Command("ARM","ARMED_STOP");await child.Command("START","SCHEDULER_STARTED");
            for(int n=0;n<150;n++)
            {
                await child.Send("STATUS");string line=await child.Expect("STATUS mode=");
                if(line.Contains("mode=RUNNING")&&line.Contains("fills=6"))break;
                Assert.True(n<149,"Mixed ordinary cycle failed: "+child.Log);await Task.Delay(200);
            }
            await child.Command("STOP","STOP_DRAINED_UNARMED");await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("MIXED_NATIVE_PASS",child.Log);
        }
        using(var child=HostChild.Start(root,cash?"mixed-cash-reopen":"mixed-reopen"))
        {
            await child.Expect("STOP_DISCONNECTED_UNARMED");await child.Command("CONNECT","CONNECTED_STOP");await child.Send("STATUS");var line=await child.Expect("STATUS mode=");Assert.Contains("mode=STOP",line);Assert.Contains("armed=False",line);
            await child.Command("START","START_REFUSED");await child.Command("QUIT","HOST_DRAINED_CREDENTIALS_CLOSED");await child.Exit(0);Assert.Contains("posts=0",child.Log);
        }
        Directory.Delete(root,true);
    }
}
