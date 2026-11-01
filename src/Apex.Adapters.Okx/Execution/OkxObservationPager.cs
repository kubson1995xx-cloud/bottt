using System.Numerics;
using System.Collections.Immutable;
namespace Apex.Adapters.Okx.Execution;

/// <summary>Bounded transport traversal only. Collected is not coverage, finality, hold release or START authority.</summary>
public sealed class OkxObservationPager
{
    private readonly OkxExecutionObservationClient client;
    private static readonly TimeSpan Budget=TimeSpan.FromSeconds(60);
    // Token-scoped restrictive budget, registered only by CollectAsync and removed in finally.
    // Allows the existing closed reads to enforce the original collection clocks after waits/signing/provider.
    // No caller can use this to extend a read budget or obtain any account/financial authority.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<CancellationToken,Func<bool>> CollectionBudgets=new();
    internal static bool WithinCollectionBudget(CancellationToken token)=>!CollectionBudgets.TryGetValue(token,out var admitted)||admitted();
    public OkxObservationPager(OkxExecutionObservationClient client){ArgumentNullException.ThrowIfNull(client);this.client=client;}
    private sealed record Row(object Value,string Key1,string Key2,string Cursor,string Digest,DateTimeOffset? SourceTime,DateTimeOffset? FillTime,string? LinkInstrument=null,string? LinkTrade=null);
    private sealed record Page(OkxObservationStatus Status,string Endpoint,string ExactQuery,string? Error,string? Digest,DateTimeOffset RequestUtc,DateTimeOffset ReceiveUtc,long RequestMono,long ReceiveMono,IReadOnlyList<Row> Rows);
    public async Task<OkxObservationCollection> CollectAsync(OkxObservationCollectionQuery query,CancellationToken ct)
    {
        var clock=client.ObservationClock;var startUtc=clock.GetUtcNow();var startMono=clock.GetTimestamp();var lastUtc=startUtc;var lastMono=startMono;
        var endUtc=startUtc;var endMono=startMono;
        var trace=new List<OkxObservationPageTrace>();var orders=new List<OkxExecutionOrderDto>();var fills=new List<OkxExecutionFillDto>();var bills=new List<OkxExecutionBillDto>();
        var limitations=new List<string>{"NoAtomicSnapshot","RetentionNotEstablished","TimeBoundaryInclusivityUnproven","CollectionNotFinancialAuthority"};
        var identities=new Dictionary<(string,string),HashSet<string>>();var links=new Dictionary<(string,string),(string,string)>();
        var opening=new HashSet<(string,string,string)>();var cursors=new HashSet<string>(StringComparer.Ordinal);
        int requests=0,occurrences=0;bool conflict=false;
        var surface=query?.Surface??OkxObservationSurface.Pending;var context=query?.Context??new(Guid.Empty,Guid.Empty);
        var original=query?.Page??new(null,null,null,null,null);var current=original;bool bookend=false;
        string? timeField=surface==OkxObservationSurface.Pending?null:surface is OkxObservationSurface.History or OkxObservationSurface.HistoryArchive?"CTime":"Ts";
        if(timeField=="CTime")limitations.Add("CreationTimeFilterNotCompletionWatermark");
        OkxObservationCollection Result(OkxObservationCollectionStatus status,string? error)=>new(conflict?OkxObservationCollectionStatus.Conflict:status,client.RequestedObservationScope,query!,startUtc,endUtc,startMono,endMono,trace,orders,fills,bills,limitations,error);
        string? collectionDenied=null;
        string? BudgetError()
        {
            if(collectionDenied is not null)return collectionDenied;
            var utc=clock.GetUtcNow();var mono=clock.GetTimestamp();
            bool rollback=utc<lastUtc||mono<lastMono;lastUtc=utc;lastMono=mono;
            if(ct.IsCancellationRequested)return collectionDenied="Canceled";
            if(rollback)return collectionDenied="ClockRollback";
            return collectionDenied=utc-startUtc>=Budget||clock.GetElapsedTime(startMono,mono)>=Budget?"CollectionBudget":null;
        }
        OkxObservationCollection Stop(string error)
        {
            var utc=clock.GetUtcNow();var mono=clock.GetTimestamp();endUtc=utc;endMono=mono;
            trace.Add(new(surface,RecoveryRequest.Endpoint(surface),RecoveryRequest.Valid(surface,current)?RecoveryRequest.Query(surface,current):"",context,trace.Count,bookend,true,OkxObservationStatus.Failed,0,null,utc,utc,mono,mono,current.Cursor,null,timeField,null,null,null,null,false,error));
            return Result(OkxObservationCollectionStatus.Incomplete,error);
        }
        if(query is null||!RecoveryRequest.Valid(surface,original)||original.Cursor is not null||context.ConnectionId==Guid.Empty||context.CaptureId==Guid.Empty)return Stop("InvalidObservationRequest");
        using var timeout=new CancellationTokenSource(Budget,clock);
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        CollectionBudgets.TryAdd(linked.Token,()=>!linked.IsCancellationRequested&&BudgetError() is null);
        try
        {
        while(true)
        {
            var error=BudgetError();if(error is not null)return Stop(error);
            if(!client.CanContinueObservation(context))return Stop("ObservationStopped");
            error=BudgetError();if(error is not null)return Stop(error);
            // Leave one final head request available before every continuation scan.
            if(requests>=20||!bookend&&requests>=19)return Stop("PageLimit");
            Page page=await ReadPage(surface,current,context,linked.Token).ConfigureAwait(false);requests=checked(requests+1);
            endUtc=page.ReceiveUtc;endMono=page.ReceiveMono;
            var next=page.Status==OkxObservationStatus.Observed&&page.Rows.Count>0&&!bookend?new OkxObservationCursor(surface,original.Instrument,OkxObservationCursorDirection.After,page.Rows[^1].Cursor):null;
            trace.Add(new(surface,page.Endpoint,page.ExactQuery,context,trace.Count,requests==1||bookend,false,page.Status,page.Rows.Count,page.Digest,page.RequestUtc,page.ReceiveUtc,page.RequestMono,page.ReceiveMono,current.Cursor,next,timeField,page.Rows.Select(x=>x.SourceTime).Min(),page.Rows.Select(x=>x.SourceTime).Max(),page.Rows.Select(x=>x.FillTime).Min(),page.Rows.Select(x=>x.FillTime).Max(),page.Status==OkxObservationStatus.Observed&&page.Rows.Count==0,page.Error));
            if(page.Status!=OkxObservationStatus.Observed)return Result(OkxObservationCollectionStatus.Incomplete,page.Error);
            occurrences=checked(occurrences+page.Rows.Count);
            var head=new HashSet<(string,string,string)>();
            // Retain ALL variants before making any cursor/bookend/resource decision.
            foreach(var row in page.Rows)
            {
                var key=(row.Key1,row.Key2);head.Add((row.Key1,row.Key2,row.Digest));
                if(!identities.TryGetValue(key,out var variants))identities.Add(key,variants=new(StringComparer.Ordinal));
                if(variants.Count>0&&!variants.Contains(row.Digest))conflict=true;
                if(row.LinkInstrument is not null&&row.LinkTrade is not null)
                {
                    var link=(row.LinkInstrument,row.LinkTrade);var payload=(row.Key2,row.Digest);
                    if(links.TryGetValue(link,out var previous)&&previous!=payload)conflict=true;
                    else links.TryAdd(link,payload);
                }
                if(variants.Add(row.Digest))
                {
                    if(row.Value is OkxExecutionOrderDto o)orders.Add(o);
                    else if(row.Value is OkxExecutionFillDto f)fills.Add(f);
                    else if(row.Value is OkxExecutionBillDto b)bills.Add(b);
                }
                if(timeField is not null&&row.SourceTime is null&&!limitations.Contains("MissingSourceTime"))limitations.Add("MissingSourceTime");
                if(row.SourceTime.HasValue&&original.BeginUtc.HasValue&&(row.SourceTime<original.BeginUtc||row.SourceTime>original.EndUtc)&&!limitations.Contains("OutsideRequestedTimeBounds"))limitations.Add("OutsideRequestedTimeBounds");
            }
            if(requests==1)opening=head;
            if(bookend&&!opening.SetEquals(head)){conflict=true;return Result(OkxObservationCollectionStatus.Conflict,"MovingBookend");}
            if(conflict)return Result(OkxObservationCollectionStatus.Conflict,"IdentityPayloadConflict");
            if(occurrences>2000)return Stop("RowLimit");
            error=BudgetError();if(error is not null)return Stop(error);
            if(!client.CanContinueObservation(context))return Stop("ObservationStopped");
            error=BudgetError();if(error is not null)return Stop(error);
            if(bookend)return Result(OkxObservationCollectionStatus.Collected,null);
            if(page.Rows.Count==0){bookend=true;current=original;continue;}
            // Local conservative ID-progress policy, not an exchange chronology guarantee.
            var numeric=page.Rows.Select(x=>BigInteger.Parse(x.Cursor,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
            if(numeric[^1]!=numeric.Min())return Stop("AmbiguousPageOrder");
            if(current.Cursor is not null&&!numeric.Any(x=>x<BigInteger.Parse(current.Cursor.Value,System.Globalization.CultureInfo.InvariantCulture)))return Stop("NoCursorProgress");
            if(!cursors.Add(next!.Value))return Stop("CursorCycle");
            current=new(original.Instrument,original.OrderId,original.BeginUtc,original.EndUtc,next,original.Limit);
        }
        }
        finally { CollectionBudgets.TryRemove(linked.Token,out _); }
    }
    public async Task<OkxAlgoObservationCollection> CollectAlgosAsync(OkxObservationAlgoPageQuery query,OkxObservationContext context,CancellationToken ct)
    {
        context??=new(Guid.Empty,Guid.Empty);
        var clock=client.ObservationClock;var startUtc=clock.GetUtcNow();var startMono=clock.GetTimestamp();var lastUtc=startUtc;var lastMono=startMono;var endUtc=startUtc;var endMono=startMono;
        var rows=new List<OkxExecutionAlgoDto>();var trace=new List<OkxAlgoObservationPageTrace>();
        var limitations=new List<string>{"NoAtomicSnapshot","RetentionNotEstablished","CollectionNotFinancialAuthority"};
        if(query?.Surface==OkxAlgoObservationSurface.History)limitations.Add("HistoryHasNoTimeFilter");
        if(query?.OrderType==OkxAlgoOrderFilter.TrailingStop)limitations.Add("TrailingStreamNotSubscribed");
        var variants=new Dictionary<(string,string),HashSet<string>>();var cursors=new HashSet<string>(StringComparer.Ordinal);var opening=new HashSet<(string,string,string)>();
        var original=query!;var current=query!;bool bookend=false,conflict=false;int requests=0,occurrences=0;string? denied=null;
        OkxAlgoObservationCollection Result(OkxObservationCollectionStatus status,string? error)=>new(conflict?OkxObservationCollectionStatus.Conflict:status,client.RequestedObservationScope,original,context,startUtc,endUtc,startMono,endMono,rows.ToImmutableArray(),trace.ToImmutableArray(),limitations.ToImmutableArray(),error);
        string? BudgetError()
        {
            if(denied is not null)return denied;var utc=clock.GetUtcNow();var mono=clock.GetTimestamp();bool rollback=utc<lastUtc||mono<lastMono;lastUtc=utc;lastMono=mono;
            if(ct.IsCancellationRequested)return denied="Canceled";
            if(rollback)return denied="ClockRollback";
            return denied=utc-startUtc>=Budget||clock.GetElapsedTime(startMono,mono)>=Budget?"CollectionBudget":null;
        }
        OkxAlgoObservationCollection Stop(string error)
        {
            endUtc=clock.GetUtcNow();endMono=clock.GetTimestamp();
            trace.Add(new(current,current is null?"":AlgoRequest.Endpoint(current.Surface),current is null?"":AlgoRequest.Query(current),context,trace.Count,bookend,true,OkxObservationStatus.Failed,0,null,endUtc,endUtc,endMono,endMono,current?.Cursor,null,false,error));
            return Result(OkxObservationCollectionStatus.Incomplete,error);
        }
        if(query is null||query.Cursor is not null||context.ConnectionId==Guid.Empty||context.CaptureId==Guid.Empty)return Stop("InvalidObservationRequest");
        using var timeout=new CancellationTokenSource(Budget,clock);using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,timeout.Token);
        CollectionBudgets.TryAdd(linked.Token,()=>!linked.IsCancellationRequested&&BudgetError() is null);
        try
        {
            while(true)
            {
                var error=BudgetError();if(error is not null)return Stop(error);
                if(!client.CanContinueObservation(context))return Stop("ObservationStopped");
                if((error=BudgetError()) is not null)return Stop(error);
                if(requests>=20||!bookend&&requests>=19)return Stop("PageLimit");
                var page=await (current.Surface==OkxAlgoObservationSurface.Pending?client.ReadPendingAlgosAsync(current,context,linked.Token):client.ReadAlgoHistoryAsync(current,context,linked.Token)).ConfigureAwait(false);requests++;
                endUtc=page.ReceiveUtc;endMono=page.ReceiveMonotonic;
                var next=page.Status==OkxObservationStatus.Observed&&page.Rows.Count>0&&!bookend?new OkxObservationAlgoCursor(original.Surface,original.OrderType,original.State,original.AlgoId,original.Instrument,OkxObservationCursorDirection.After,page.Rows[^1].AlgoId):null;
                trace.Add(new(current,page.Endpoint,page.ExactQuery,context,trace.Count,requests==1||bookend,false,page.Status,page.Rows.Count,page.ResponseDigest,page.RequestUtc,page.ReceiveUtc,page.RequestMonotonic,page.ReceiveMonotonic,current.Cursor,next,page.Status==OkxObservationStatus.Observed&&page.Rows.Count==0,page.ErrorCode));
                if(page.Status!=OkxObservationStatus.Observed)return Result(OkxObservationCollectionStatus.Incomplete,page.ErrorCode);
                occurrences=checked(occurrences+page.Rows.Count);var head=new HashSet<(string,string,string)>();
                foreach(var row in page.Rows)
                {
                    var key=(row.InstId,row.AlgoId);head.Add((row.InstId,row.AlgoId,row.PayloadDigest));
                    if(!variants.TryGetValue(key,out var set))variants.Add(key,set=new(StringComparer.Ordinal));
                    if(set.Count>0&&!set.Contains(row.PayloadDigest))conflict=true;
                    if(set.Add(row.PayloadDigest))rows.Add(row);
                }
                if(requests==1)opening=head;
                if(bookend&&!opening.SetEquals(head)){conflict=true;return Result(OkxObservationCollectionStatus.Conflict,"MovingBookend");}
                if(conflict)return Result(OkxObservationCollectionStatus.Conflict,"IdentityPayloadConflict");
                if(occurrences>2000)return Stop("RowLimit");
                if((error=BudgetError()) is not null)return Stop(error);
                if(!client.CanContinueObservation(context))return Stop("ObservationStopped");
                if((error=BudgetError()) is not null)return Stop(error);
                if(bookend)return Result(OkxObservationCollectionStatus.Collected,null);
                if(page.Rows.Count==0){bookend=true;current=original;continue;}
                var numeric=page.Rows.Select(x=>BigInteger.Parse(x.AlgoId,System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                if(numeric[^1]!=numeric.Min())return Stop("AmbiguousPageOrder");
                if(current.Cursor is not null&&!numeric.Any(x=>x<BigInteger.Parse(current.Cursor.Value,System.Globalization.CultureInfo.InvariantCulture)))return Stop("NoCursorProgress");
                if(!cursors.Add(next!.Value))return Stop("CursorCycle");
                current=new(original.Surface,original.OrderType,original.State,original.AlgoId,original.Instrument,next,original.Limit);
            }
        }
        finally {CollectionBudgets.TryRemove(linked.Token,out _);}
    }
    private async Task<Page> ReadPage(OkxObservationSurface surface,OkxObservationPageQuery query,OkxObservationContext context,CancellationToken ct)
    {
        var window=surface is OkxObservationSurface.HistoryArchive or OkxObservationSurface.FillsArchive or OkxObservationSurface.BillsArchive?OkxObservationWindow.Archive:OkxObservationWindow.Recent;
        Page Box<T>(OkxObservationRead<T> read,Func<T,Row> row)=>new(read.Status,read.Endpoint,read.ExactQuery,read.ErrorCode,read.ResponseDigest,read.RequestUtc,read.ReceiveUtc,read.RequestMonotonic,read.ReceiveMonotonic,read.Rows.Select(row).ToArray());
        Row Order(OkxExecutionOrderDto o)=>new(o,o.InstId,o.OrdId,o.OrdId,o.PayloadDigest,surface==OkxObservationSurface.Pending?null:o.CTime.Utc,o.FillTime.Utc);
        if(surface==OkxObservationSurface.Pending)return Box(await client.ReadPendingAsync(query,context,ct).ConfigureAwait(false),Order);
        if(surface is OkxObservationSurface.History or OkxObservationSurface.HistoryArchive)return Box(await client.ReadHistoryAsync(window,query,context,ct).ConfigureAwait(false),Order);
        if(surface is OkxObservationSurface.Fills or OkxObservationSurface.FillsArchive)
            return Box(await client.ReadFillsAsync(window,query,context,ct).ConfigureAwait(false),f=>new(f,"",f.BillId,f.BillId,f.PayloadDigest,f.Ts.Utc,f.FillTime.Utc,string.IsNullOrEmpty(f.TradeId)?null:f.InstId,string.IsNullOrEmpty(f.TradeId)?null:f.TradeId));
        return Box(await client.ReadBillsAsync(window,query,context,ct).ConfigureAwait(false),b=>new(b,"",b.BillId,b.BillId,b.PayloadDigest,b.Ts.Utc,b.FillTime.Utc));
    }
}
