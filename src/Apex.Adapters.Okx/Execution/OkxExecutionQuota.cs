using System.Collections.Concurrent;
namespace Apex.Adapters.Okx.Execution;

// Conservative scheduling namespace only, not authenticated identity. Never cleared on client disposal.
internal static class OkxExecutionQuota
{
    internal sealed class Lane
    {
        internal readonly SemaphoreSlim Gate=new(1,1);
        internal DateTimeOffset Next;
        internal TimeProvider? Clock;
        internal long NextMonotonic;
    }
    private static readonly ConcurrentDictionary<(string Environment,string Uid,string Instrument),Lane> Detail=new();
    internal static Lane OrderDetail(OkxExecutionScope scope,string instrument)=>Detail.GetOrAdd((scope.Environment,scope.Uid,instrument),_=>new());
}
