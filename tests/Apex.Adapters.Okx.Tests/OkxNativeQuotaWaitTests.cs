using System.Reflection;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public sealed class OkxNativeQuotaWaitTests
{
    [Fact]
    public async Task Production_delay_rechecks_both_clocks_after_native_timer_fires_early()
    {
        using var f=new OkxExecutionObservationHttpFixture();var clock=new EarlyClock();
        await using var client=OkxExecutionObservationClient.ForTests(f,clock,f.Scope,f,f.Fingerprint,f);
        var delay=(Func<TimeSpan,CancellationToken,Task>)typeof(OkxExecutionObservationClient).GetField("delay",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(client)!;
        var task=delay(TimeSpan.FromMilliseconds(600),default);
        clock.Fire(TimeSpan.FromMilliseconds(599));await Task.Delay(25);
        Assert.False(task.IsCompleted,"Native timer return before the requested quota is not completed waiting.");
        clock.Fire(TimeSpan.FromMilliseconds(2));await task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(clock.GetTimestamp()>=TimeSpan.FromMilliseconds(600).Ticks);
    }
    private sealed class EarlyClock:TimeProvider
    {
        private long ticks;private readonly object sync=new();private readonly List<Pending> timers=new();
        public override DateTimeOffset GetUtcNow()=>DateTimeOffset.UnixEpoch+TimeSpan.FromTicks(Interlocked.Read(ref ticks));
        public override long GetTimestamp()=>Interlocked.Read(ref ticks);
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback,object? state,TimeSpan due,TimeSpan period)
        {lock(sync){var timer=new Pending(callback,state);timers.Add(timer);return timer;}}
        internal void Fire(TimeSpan elapsed)
        {
            Interlocked.Add(ref ticks,elapsed.Ticks);Pending[] current;
            lock(sync){current=timers.ToArray();timers.Clear();}
            foreach(var t in current)t.Callback(t.State);
        }
        private sealed class Pending(TimerCallback callback,object? state):ITimer
        {internal readonly TimerCallback Callback=callback;internal readonly object? State=state;public bool Change(TimeSpan due,TimeSpan period)=>true;public void Dispose(){}public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
    }
}
