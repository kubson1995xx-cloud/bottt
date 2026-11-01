using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

// Independent retained tables/examples are frozen in recovery-schema.json BEFORE parsing code.
// Replacing a real read by Failed, dropping metadata or relaxing its exact schema breaks these controls.
public sealed class OkxExecutionRecoveryObservationTests
{
    private static readonly DateTimeOffset Begin=DateTimeOffset.FromUnixTimeMilliseconds(1793448000000), End=DateTimeOffset.FromUnixTimeMilliseconds(1793534400000);
    private static JsonNode Oracle()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(System.IO.Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/recovery-schema.json")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/recovery-schema.json")))!;
    }
    private static JsonNode Wire(string name,bool extended=false)=>Oracle()["endpoints"]![name]![extended?"syntheticExtension":"control"] is var n&&extended?n!["control"]!.DeepClone():n!.DeepClone();
    private static string Endpoint(string name)=>Oracle()["endpoints"]![name]!["endpoint"]!.GetValue<string>();
    private static string Path(string name,int limit=100)=>Endpoint(name)+(name=="order"?"?clOrdId=Ab9&instId=BTC-USDC&ordId=987654":name=="pending"?"?instType=SPOT&limit="+limit:name.StartsWith("bills")?"?begin=1793448000000&end=1793534400000&limit="+limit:"?begin=1793448000000&end=1793534400000&instType=SPOT&limit="+limit);
    private static void Add(OkxExecutionObservationHttpFixture f,string path,JsonNode wire,int status=200)=>AddRaw(f,path,wire.ToJsonString(),status);
    private static void AddRaw(OkxExecutionObservationHttpFixture f,string path,string body,int status=200)=>f.Steps.Enqueue((path,_=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body,Encoding.UTF8,"application/json")})));
    private sealed record Seen(OkxObservationStatus Status,string? Error,IReadOnlyList<object> Rows,string Query,object Envelope);
    private static Seen Box<T>(OkxObservationRead<T> r)=>new(r.Status,r.ErrorCode,r.Rows.Cast<object>().ToArray(),r.ExactQuery,r);
    private static async Task<Seen> Read(OkxExecutionObservationClient c,OkxExecutionObservationHttpFixture f,string name,OkxObservationPageQuery? page=null,CancellationToken ct=default)
    {
        page??=new(null,null,name=="pending"?null:Begin,name=="pending"?null:End,null);
        return name switch
        {
            "order"=>Box(await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,ct)),
            "pending"=>Box(await c.ReadPendingAsync(page,f.Context,ct)),
            "history"=>Box(await c.ReadHistoryAsync(OkxObservationWindow.Recent,page,f.Context,ct)),
            "historyArchive"=>Box(await c.ReadHistoryAsync(OkxObservationWindow.Archive,page,f.Context,ct)),
            "fills"=>Box(await c.ReadFillsAsync(OkxObservationWindow.Recent,page,f.Context,ct)),
            "fillsArchive"=>Box(await c.ReadFillsAsync(OkxObservationWindow.Archive,page,f.Context,ct)),
            "bills"=>Box(await c.ReadBillsAsync(OkxObservationWindow.Recent,page,f.Context,ct)),
            _=>Box(await c.ReadBillsAsync(OkxObservationWindow.Archive,page,f.Context,ct))
        };
    }
    public static IEnumerable<object[]> Endpoints()=>new[]{"order","pending","history","historyArchive","fills","fillsArchive","bills","billsArchive"}.Select(x=>new object[]{x});
    public static IEnumerable<object[]> OfficialCases()=>Endpoints().SelectMany(x=>new[]{new object[]{x[0],"Demo"},new object[]{x[0],"Live"}});
    [Theory][MemberData(nameof(OfficialCases))]
    public async Task ReadRecovery_PopulatedOfficialEndpoint(string name,string env)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Scope=new(env,f.Scope.Uid,f.Scope.Uid,"USD","official-"+env);
        var w=Wire(name);Add(f,Path(name),w);await using var c=f.Client();Assert.Equal(0,f.Opens);Assert.Empty(f.Sent);
        var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);
        Assert.Equal(1,f.Opens);Assert.Single(f.Sent);Assert.Equal(Path(name)[Endpoint(name).Length..],r.Query);
        var actual=JsonSerializer.SerializeToNode(row)!;var original=w["data"]![0]!;
        if(name.StartsWith("bills"))
        { Assert.Equal("623950854533513219",actual["BillId"]!.GetValue<string>());Assert.Equal("isolated",actual["MgnMode"]!.GetValue<string>());Assert.Equal("0.0219338232210000",actual["BalChg"]!["Raw"]!.GetValue<string>()); }
        else
        {Assert.Equal("",actual["ClOrdId"]!.GetValue<string>());Assert.Equal("BTC-USDT",actual["InstId"]!.GetValue<string>());Assert.Equal("USDT",actual["TradeQuoteCcy"]!.GetValue<string>());Assert.Equal(name=="pending"?"0":"-0.00000192834",actual["Fee"]!["Raw"]!.GetValue<string>());}
        Assert.Equal(original["fillTime"]!.GetValue<string>(),actual["FillTime"]!["Raw"]!.GetValue<string>());
        Assert.Matches("^[0-9a-f]{64}$",actual["PayloadDigest"]!.GetValue<string>());f.Done();
    }
    [Fact] public async Task ReadRecovery_ExactQueryAndCursorNamespaces()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        Add(f,"/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC&ordId=987654",Wire("order",true));
        var d=await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,default);Assert.Equal(OkxObservationStatus.Observed,d.Status);Assert.Equal("DifferentClient",Assert.Single(d.Rows).ClOrdId);Assert.Contains("clOrdId=Ab9",d.ExactQuery);
        Add(f,"/api/v5/trade/orders-pending?after=900&instType=SPOT&limit=1",Wire("pending"));
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadPendingAsync(new(null,null,null,null,new(OkxObservationSurface.Pending,null,OkxObservationCursorDirection.After,"900"),1),f.Context,default)).Status);
        Add(f,"/api/v5/trade/orders-history?after=900&begin=1793448000000&end=1793534400000&instType=SPOT&limit=100",Wire("history"));
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadHistoryAsync(OkxObservationWindow.Recent,new(null,null,Begin,End,new(OkxObservationSurface.History,null,OkxObservationCursorDirection.After,"900")),f.Context,default)).Status);
        Add(f,"/api/v5/trade/fills-history?before=700&begin=1793448000000&end=1793534400000&instId=BTC-USDC&instType=SPOT&limit=100&ordId=987654",Wire("fillsArchive"));
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadFillsAsync(OkxObservationWindow.Archive,new("BTC-USDC","987654",Begin,End,new(OkxObservationSurface.FillsArchive,"BTC-USDC",OkxObservationCursorDirection.Before,"700")),f.Context,default)).Status);
        Add(f,"/api/v5/account/bills-archive?after=700&begin=1793448000000&end=1793534400000&limit=100",Wire("billsArchive"));
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadBillsAsync(OkxObservationWindow.Archive,new(null,null,Begin,End,new(OkxObservationSurface.BillsArchive,null,OkxObservationCursorDirection.After,"700")),f.Context,default)).Status);f.Done();
    }
    [Fact] public async Task ReadRecovery_InvalidClosedRequestsNeverOpen()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        foreach(var bad in new[]{"","1&x=y","１２","123456789012345678901234567890123"})Assert.Equal("InvalidObservationRequest",Assert.Throws<ArgumentException>(()=>new OkxObservationOrderQuery("BTC-USDC",bad,null)).Message);
        Assert.Throws<ArgumentException>(()=>new OkxObservationPageQuery(null,null,Begin.AddTicks(1),End,null));Assert.Throws<ArgumentException>(()=>new OkxObservationPageQuery(null,null,Begin,End,null,101));
        Assert.Throws<ArgumentException>(()=>new OkxObservationCursor((OkxObservationSurface)99,null,OkxObservationCursorDirection.After,"1"));
        var invalids=new[]{await c.ReadOrderAsync(null!,f.Context,default),await c.ReadPendingAsync(new(null,null,Begin,End,null),f.Context,default),await c.ReadHistoryAsync((OkxObservationWindow)99,new(null,null,Begin,End,null),f.Context,default),await c.ReadHistoryAsync(0,new(null,"1",Begin,End,null),f.Context,default),await c.ReadPendingAsync(new(null,null,null,null,new(OkxObservationSurface.Fills,null,0,"1")),f.Context,default)};
        foreach(var r in invalids){Assert.Equal("InvalidObservationRequest",r.ErrorCode);Assert.Empty(r.Rows);}
        Assert.Equal("InvalidObservationRequest",(await c.ReadBillsAsync(0,new("BTC-USDC",null,Begin,End,null),f.Context,default)).ErrorCode);
        Assert.Equal("InvalidObservationRequest",(await c.ReadFillsAsync(0,null!,f.Context,default)).ErrorCode);Assert.Empty(f.Sent);Assert.Equal(0,f.Opens);
    }
    public static IEnumerable<object[]> Fields()=>Oracle()["endpoints"]!.AsObject().SelectMany(e=>e.Value!["fields"]!.AsArray().Select(f=>new object[]{e.Key,f!["field"]!.GetValue<string>(),f["type"]!.GetValue<string>(),f["policy"]!.GetValue<string>(),f["presence"]!.GetValue<string>()}));
    private static (JsonObject Parent,string Key) At(JsonNode w,string field)
    {
        var row=w["data"]![0]!.AsObject();if(!field.Contains('.'))return(row,field);
        var a=field.Split('.');return(a[0].EndsWith("[]")?row["attachAlgoOrds"]![0]!.AsObject():row["linkedAlgoOrd"]!.AsObject(),a[1]);
    }
    [Theory][MemberData(nameof(Fields))]
    public async Task ReadRecovery_NestedAndDiscardedSchema(string name,string field,string type,string policy,string presence)
    {
        Assert.NotEmpty(policy);using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        var w=Wire(name,true);Add(f,Path(name),w);Assert.Equal(OkxObservationStatus.Observed,(await Read(c,f,name)).Status);
        var at=At(w,field);at.Parent[at.Key]=type=="String"?JsonValue.Create(false):JsonValue.Create("");Add(f,Path(name),w);
        var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal("InvalidPayload",r.Error);Assert.Empty(r.Rows);
        w=Wire(name,true);at=At(w,field);at.Parent.Remove(at.Key);Add(f,Path(name),w);r=await Read(c,f,name);Assert.Equal(presence.StartsWith("required")?OkxObservationStatus.Failed:OkxObservationStatus.Observed,r.Status);f.Done();
    }
    [Theory][MemberData(nameof(Endpoints))]
    public async Task ReadRecovery_MissingEmptyAndZeroAreDistinct(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var values=new List<OkxDecimal>();var times=new List<OkxTimestamp>();var digests=new List<string>();
        foreach(var value in new string?[]{null,"","0"})
        {
            var w=Wire(name);var row=w["data"]![0]!.AsObject();if(value is null){row.Remove("fee");row.Remove("fillTime");}else{row["fee"]=value;row["fillTime"]=value;}
            Add(f,Path(name),w);var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Observed,r.Status);var x=Assert.Single(r.Rows);
            values.Add((OkxDecimal)x.GetType().GetProperty("Fee")!.GetValue(x)!);times.Add((OkxTimestamp)x.GetType().GetProperty("FillTime")!.GetValue(x)!);digests.Add((string)x.GetType().GetProperty("PayloadDigest")!.GetValue(x)!);
        }
        Assert.Null(values[0].Raw);Assert.Null(values[0].Value);Assert.Equal("",values[1].Raw);Assert.Null(values[1].Value);Assert.Equal(0m,values[2].Value);
        Assert.Null(times[0].Raw);Assert.Null(times[1].Utc);Assert.Equal(DateTimeOffset.UnixEpoch,times[2].Utc);Assert.Equal(3,digests.Distinct().Count());f.Done();
    }
    [Theory][MemberData(nameof(Endpoints))]
    public async Task ReadRecovery_ForeignAndSystemRowsRemainRaw(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();Add(f,Path(name),Wire(name,true));var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Observed,r.Status);
        var row=Assert.Single(r.Rows);var serialized=JsonSerializer.Serialize(row);Assert.DoesNotContain("PRIVATE_DISCARDED_SENTINEL",serialized+row);Assert.DoesNotContain("notes",serialized,StringComparison.OrdinalIgnoreCase);f.Done();
    }
    [Theory][MemberData(nameof(Endpoints))]
    public async Task ReadRecovery_SingletonVersusLimitOnePage(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        foreach(var count in new[]{0,1,2})
        {
            var w=Wire(name);var row=w["data"]![0]!.DeepClone();w["data"]=new JsonArray();for(int n=0;n<count;n++)w["data"]!.AsArray().Add(row.DeepClone());Add(f,Path(name,1),w);
            var r=await Read(c,f,name,new(null,null,name=="pending"?null:Begin,name=="pending"?null:End,null,1));bool ok=name=="order"?count==1:count<=1;
            Assert.Equal(ok?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);Assert.Equal(ok?count:0,r.Rows.Count);if(!ok)Assert.Equal("Cardinality",r.Error);
        }
        f.Done();
    }
    [Theory][InlineData("order","cancelSourceReason")][InlineData("order","attachAlgoOrds[].failReason")][InlineData("bills","notes")]
    public async Task ReadRecovery_PrivateDiscardedTextChangesDigestNotPublicText(string name,string field)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var digests=new List<string>();
        foreach(var text in new[]{"PRIVATE_ONE","PRIVATE_TWO"})
        {var w=Wire(name,true);var at=At(w,field);at.Parent[at.Key]=text;Add(f,Path(name),w);var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);Assert.DoesNotContain("PRIVATE_",JsonSerializer.Serialize(row)+row);digests.Add((string)row.GetType().GetProperty("PayloadDigest")!.GetValue(row)!);}
        Assert.NotEqual(digests[0],digests[1]);
        var bad=Wire(name,true);var a=At(bad,field);a.Parent[a.Key]="SURROGATE_MARK";AddRaw(f,Path(name),bad.ToJsonString().Replace("SURROGATE_MARK","\\uD800"));var failed=await Read(c,f,name);Assert.Equal("InvalidPayload",failed.Error);Assert.Empty(failed.Rows);f.Done();
    }
    [Theory][InlineData("51603",200)][InlineData("50011",200)][InlineData("0",429)][InlineData("0",503)][InlineData("0",302)]
    public async Task ReadRecovery_ErrorAndResourceFailuresArePrivate(string code,int status)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();AddRaw(f,Path("fills"),"{\"code\":\""+code+"\",\"msg\":\"PRIVATE_SENTINEL\",\"data\":[]}",status);
        var r=await Read(c,f,"fills");Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);Assert.DoesNotContain("PRIVATE_SENTINEL",JsonSerializer.Serialize(r.Envelope)+r.Envelope);Assert.Single(f.Sent);f.Done();
    }

    private static JsonNode Rows(string name,params string[] ids)
    {
        var w=Wire(name);var prototype=w["data"]![0]!.DeepClone();w["data"]=new JsonArray();
        foreach(var id in ids){var row=prototype.DeepClone();row[name.StartsWith("fills")||name.StartsWith("bills")?"billId":"ordId"]=id;if(name.StartsWith("fills"))row["tradeId"]="trade"+id;w["data"]!.AsArray().Add(row);}return w;
    }
    private static string After(string name,string id)=>Path(name).Replace("?","?after="+id+"&");
    private static OkxObservationCollectionQuery CollectionQuery(OkxExecutionObservationHttpFixture f,string name)=>new(name switch {"pending"=>OkxObservationSurface.Pending,"history"=>OkxObservationSurface.History,"historyArchive"=>OkxObservationSurface.HistoryArchive,"fills"=>OkxObservationSurface.Fills,"fillsArchive"=>OkxObservationSurface.FillsArchive,"bills"=>OkxObservationSurface.Bills,_=>OkxObservationSurface.BillsArchive},new(null,null,name=="pending"?null:Begin,name=="pending"?null:End,null),f.Context);
    public static IEnumerable<object[]> PagedEndpoints()=>Endpoints().Where(x=>(string)x[0]!="order");
    [Theory][MemberData(nameof(PagedEndpoints))]
    public async Task Collect_ShortNonemptyThenEmpty(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows(name,"900","800");Add(f,Path(name),head);Add(f,After(name,"800"),Rows(name,"700"));Add(f,After(name,"700"),Rows(name));Add(f,Path(name),head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,name),default);Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);
        Assert.Equal(3,r.Orders.Count+r.Fills.Count+r.Bills.Count);Assert.Equal(4,r.Trace.Count);Assert.True(r.Trace[0].IsBookend);Assert.True(r.Trace[3].IsBookend);Assert.True(r.Trace[2].EmptyTerminal);
        Assert.Equal("800",r.Trace[0].NextCursor!.Value);Assert.Equal("700",r.Trace[1].NextCursor!.Value);Assert.All(r.Trace,t=>Assert.False(t.IsLocalStop));
        Assert.Equal(new[]{0,0,600,1200}.Select(ms=>f.Sent[0].Utc.AddMilliseconds(ms)),r.Trace.Select(x=>x.RequestUtc));Assert.Equal(new[]{0,0,600,1200}.Select(ms=>TimeSpan.FromMilliseconds(ms).Ticks),r.Trace.Select(x=>x.RequestMonotonic));Assert.Equal(f.Sent.Select(x=>x.Utc),r.Trace.Select(x=>x.ReceiveUtc));Assert.Equal(r.Trace[^1].ReceiveUtc,r.ReceiveUtc);
        Assert.Contains("NoAtomicSnapshot",r.Limitations);Assert.Contains("RetentionNotEstablished",r.Limitations);Assert.Contains("CollectionNotFinancialAuthority",r.Limitations);Assert.Contains("TimeBoundaryInclusivityUnproven",r.Limitations);
        if(name.StartsWith("history")){Assert.Equal("CTime",r.Trace[0].ObservedTimeField);Assert.Contains("CreationTimeFilterNotCompletionWatermark",r.Limitations);}else Assert.Equal(name=="pending"?null:"Ts",r.Trace[0].ObservedTimeField);
        Assert.DoesNotContain(typeof(OkxObservationCollection).GetProperties(),p=>p.Name is "Complete" or "Ready" or "Final" or "CanRelease");f.Done();
    }
    [Fact] public async Task Collect_IdenticalOverlapConverges()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows("history","900","800");Add(f,Path("history"),head);Add(f,After("history","800"),Rows("history","800","700"));Add(f,After("history","700"),Rows("history"));Add(f,Path("history"),head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"history"),default);Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Equal(new[]{"900","800","700"},r.Orders.Select(x=>x.OrdId));Assert.Equal(4,r.Trace.Count);f.Done();
    }
    [Fact] public async Task Collect_NonCursorConflictSurvivesEqualBookends()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows("history","900","800");var middle=Rows("history","900","700");middle["data"]![0]!["fee"]="-0.4";
        Add(f,Path("history"),head);Add(f,After("history","800"),middle);Add(f,After("history","700"),Rows("history"));Add(f,Path("history"),head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"history"),default);Assert.Equal(OkxObservationCollectionStatus.Conflict,r.Status);Assert.Equal("IdentityPayloadConflict",r.ErrorCode);Assert.Equal(4,r.Orders.Count);Assert.Equal(2,r.Orders.Count(x=>x.OrdId=="900"));Assert.Equal(2,f.Sent.Count);Assert.Equal(2,f.Steps.Count);Assert.Equal(2,r.Trace.Count);
    }
    [Theory][InlineData("fills")][InlineData("bills")]
    public async Task Collect_FillAndBillIdentityNamespaces(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows(name,"900");var older=Rows(name,"800");head["data"]![0]!["tradeId"]="-2";older["data"]![0]!["tradeId"]="0";head["data"]![0]!["fillTime"]="1708587373369";older["data"]![0]!["fillTime"]="1708587373360";
        Add(f,Path(name),head);Add(f,After(name,"900"),older);Add(f,After(name,"800"),Rows(name));Add(f,Path(name),head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,name),default);Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Equal("900",r.Trace[1].RequestedCursor!.Value);Assert.Equal("800",r.Trace[1].NextCursor!.Value);Assert.NotEqual(r.Trace[0].MinimumObservedTime,r.Trace[0].MinimumFillTime);Assert.Equal(r.Trace[0].MinimumObservedTime,r.Trace[1].MinimumObservedTime);Assert.NotEqual(r.Trace[0].MinimumFillTime,r.Trace[1].MinimumFillTime);f.Done();
    }
    [Theory][InlineData("same-link")][InlineData("different-instrument")]
    public async Task Collect_FillLinkConflictPreservesVariants(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows("fills","900");var next=Rows("fills","800");next["data"]![0]!["tradeId"]=head["data"]![0]!["tradeId"]!.DeepClone();if(change=="different-instrument")next["data"]![0]!["instId"]="ETH-USDT";
        Add(f,Path("fills"),head);Add(f,After("fills","900"),next);
        if(change=="different-instrument"){Add(f,After("fills","800"),Rows("fills"));Add(f,Path("fills"),head);}
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"fills"),default);Assert.Equal(change=="same-link"?OkxObservationCollectionStatus.Conflict:OkxObservationCollectionStatus.Collected,r.Status);Assert.Equal(2,r.Fills.Count);f.Done();
    }
    [Theory][InlineData("no-progress")][InlineData("ambiguous")][InlineData("cycle")][InlineData("failed-final")][InlineData("failed-bookend")][InlineData("page-cap")]
    public async Task Collect_FailureLoopAndCapsNeverTruncate(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();Add(f,Path("pending"),Rows("pending","900"));
        if(change=="no-progress")Add(f,After("pending","900"),Rows("pending","900"));
        if(change=="ambiguous")Add(f,After("pending","900"),Rows("pending","700","800"));
        if(change=="cycle"){Add(f,After("pending","900"),Rows("pending","800"));Add(f,After("pending","800"),Rows("pending","900"));}
        if(change=="failed-final")AddRaw(f,After("pending","900"),"PRIVATE_BAD",503);
        if(change=="failed-bookend"){Add(f,After("pending","900"),Rows("pending"));AddRaw(f,Path("pending"),"{\"code\":\"51603\",\"msg\":\"PRIVATE_SENTINEL\",\"data\":[]}");}
        if(change=="page-cap")for(int n=900;n>882;n--)Add(f,After("pending",n.ToString()),Rows("pending",(n-1).ToString()));
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"pending"),default);Assert.Equal(OkxObservationCollectionStatus.Incomplete,r.Status);Assert.NotEmpty(r.Orders);Assert.NotNull(r.ErrorCode);Assert.True(f.Sent.Count<=20);Assert.DoesNotContain("PRIVATE_",JsonSerializer.Serialize(r)+r);
        if(change=="page-cap"){Assert.Equal("PageLimit",r.ErrorCode);Assert.True(r.Trace[^1].IsLocalStop);Assert.Null(r.Trace[^1].ResponseDigest);Assert.False(r.Trace[^1].EmptyTerminal);}f.Done();
    }
    [Theory][InlineData("none")][InlineData("utc-equality")][InlineData("mono-equality")][InlineData("utc-rollback")][InlineData("mono-rollback")][InlineData("cancel")][InlineData("stop")][InlineData("dispose")]
    public async Task Collect_IndependentBudgetAndLifetimeNeverRenew(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();using var cancel=new CancellationTokenSource();await using var c=f.Client();var start=f.Clock.Utc;Task? disposing=null;
        Add(f,Path("pending"),Rows("pending","900"));
        // Transition at collector's post-read continuation fence, after first read has succeeded.
        int responses=0;f.OnFence=()=>{if(f.Sent.Count==1&&++responses==2){switch(change){case "utc-equality":f.Clock.Utc=start+TimeSpan.FromSeconds(60);break;case "mono-equality":f.Clock.Mono=TimeSpan.FromSeconds(60).Ticks;break;case "utc-rollback":f.Clock.Utc=start-TimeSpan.FromSeconds(1);break;case "mono-rollback":f.Clock.Mono=-1;break;case "cancel":cancel.Cancel();break;case "stop":f.Allowed=false;break;case "dispose":disposing=c.DisposeAsync().AsTask();break;}}};
        if(change=="none"){Add(f,After("pending","900"),Rows("pending"));Add(f,Path("pending"),Rows("pending","900"));}
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"pending"),cancel.Token);Assert.Equal(change=="none"?OkxObservationCollectionStatus.Collected:OkxObservationCollectionStatus.Incomplete,r.Status);Assert.Equal(change=="none"?3:1,f.Sent.Count);if(disposing is not null)await disposing;f.Done();
    }
    [Theory][InlineData("initial-empty-moves")][InlineData("final-head-moves")][InlineData("empty-stable")][InlineData("missing-time")]
    public async Task Collect_MovingEmptyAndRetentionRemainObservational(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows("history",change.StartsWith("initial-empty")||change=="empty-stable"?[]:["900"]);if(change=="missing-time")head["data"]![0]!.AsObject().Remove("cTime");
        Add(f,Path("history"),head);if(head["data"]!.AsArray().Count>0)Add(f,After("history","900"),Rows("history"));Add(f,Path("history"),change.Contains("moves")?Rows("history","950"):head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"history"),default);Assert.Equal(change.Contains("moves")?OkxObservationCollectionStatus.Conflict:OkxObservationCollectionStatus.Collected,r.Status);Assert.Contains("RetentionNotEstablished",r.Limitations);Assert.Contains("CreationTimeFilterNotCompletionWatermark",r.Limitations);if(change=="missing-time"){Assert.Contains("MissingSourceTime",r.Limitations);Assert.Null(r.Trace[0].MinimumObservedTime);}f.Done();
    }
    private sealed class DispatchFence : IOkxFinalDispatchFence
    {
        internal bool Allowed=true;internal Action? OnCheck;internal int Checks;
        public bool CanDispatch(OkxPreparedRequest request,DateTimeOffset utc){Checks++;OnCheck?.Invoke();return Allowed;}
    }
    private static OkxExecutionClient Old(OkxExecutionObservationHttpFixture f,DispatchFence fence)=>OkxExecutionClient.ForTests(f,f.Clock,f.Scope,f,f.Fingerprint,fence,(t,ct)=>{ct.ThrowIfCancellationRequested();f.Clock.Advance(t);return Task.CompletedTask;});
    private static JsonNode OldControl(){var w=Wire("order");var r=w["data"]![0]!;r["instId"]="BTC-USDC";r["ordId"]="987654";r["clOrdId"]="Ab9";r["tradeQuoteCcy"]="USD";return w;}
    private const string DetailPath="/api/v5/trade/order?clOrdId=Ab9&instId=BTC-USDC&ordId=987654";
    [Theory][InlineData(true)][InlineData(false)]
    public async Task DetailQuota_OldLookupAndNewReadShareAcrossBindings(bool oldFirst)
    {
        var clock=new ObservationTestClock();using var a=new OkxExecutionObservationHttpFixture(clock);using var b=new OkxExecutionObservationHttpFixture(clock);b.Scope=new("Demo",a.Scope.Uid,a.Scope.Uid,"EUR","different-binding");
        var fence=new DispatchFence();await using var old=Old(a,fence);await using var fresh=b.Client();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=oldFirst?a:b;var second=oldFirst?b:a;
        first.Steps.Enqueue((DetailPath,async ct=>{await release.Task.WaitAsync(ct);return new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent((oldFirst?OldControl():Wire("order")).ToJsonString())};}));
        Add(second,DetailPath,oldFirst?Wire("order"):OldControl());Task<OkxOrderObservation>? lookup=null;Task<OkxObservationRead<OkxExecutionOrderDto>>? detail=null;
        try
        {
            if(oldFirst)lookup=old.LookupAsync("BTC-USDC","987654","Ab9",default);else detail=fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default);
            Assert.Single(first.Sent);Assert.Equal(1,first.Opens);
            if(oldFirst)detail=fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default);else lookup=old.LookupAsync("BTC-USDC","987654","Ab9",default);
            Assert.Empty(second.Sent);Assert.Equal(0,second.Opens);
        }
        finally { release.TrySetResult(); }
        Assert.Equal("Observed",(await lookup!).Reason);Assert.Equal(OkxObservationStatus.Observed,(await detail!).Status);Assert.Single(second.Sent);
        Assert.True(second.Sent[0].Utc-first.Sent[0].Utc>=TimeSpan.FromMilliseconds(40));Assert.True(second.Sent[0].Mono-first.Sent[0].Mono>=TimeSpan.FromMilliseconds(40).Ticks);a.Done();b.Done();
    }
    [Theory][InlineData("none")][InlineData("revoke-new")][InlineData("revoke-old")][InlineData("expire-new")][InlineData("expire-old")][InlineData("dispose-new")]
    public async Task DetailQuota_QueuedStopExpiryAndDispose(string change)
    {
        var clock=new ObservationTestClock();using var a=new OkxExecutionObservationHttpFixture(clock);using var b=new OkxExecutionObservationHttpFixture(clock);b.Scope=new("Demo",a.Scope.Uid,a.Scope.Uid,"USD","different-binding");
        var fence=new DispatchFence();await using var old=Old(a,fence);await using var fresh=b.Client();
        Add(a,DetailPath,OldControl());Assert.Equal("Observed",(await old.LookupAsync("BTC-USDC","987654","Ab9",default)).Reason);
        Add(b,DetailPath,Wire("order"));Assert.Equal(OkxObservationStatus.Observed,(await fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default)).Status);
        Assert.True(b.Sent[0].Utc-a.Sent[0].Utc>=TimeSpan.FromMilliseconds(40));clock.Advance(TimeSpan.FromMilliseconds(600));
        if(change.EndsWith("old"))
        {
            a.OnOpen=()=>{if(change=="revoke-old")fence.Allowed=false;else clock.Utc+=TimeSpan.FromSeconds(10);};Add(a,DetailPath,OldControl());
            var r=await old.LookupAsync("BTC-USDC","987654","Ab9",default);Assert.Equal("Unknown",r.Reason);Assert.Single(a.Sent);Assert.Equal(2,a.Opens);Assert.Single(a.Steps);
        }
        else
        {
            Task? disposing=null;int afterOpen=0;b.OnOpen=()=>afterOpen=0;b.OnFence=()=>{if(b.Opens<2)return;if(++afterOpen==2){if(change=="revoke-new")b.Allowed=false;if(change=="expire-new")clock.Mono+=TimeSpan.FromSeconds(10).Ticks;if(change=="dispose-new")disposing=fresh.DisposeAsync().AsTask();}};
            Add(b,DetailPath,Wire("order"));var r=await fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default);Assert.Equal(change=="none"?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);Assert.Equal(2,b.Opens);Assert.True(afterOpen>=2);Assert.Equal(change=="none"?2:1,b.Sent.Count);if(disposing is not null)await disposing;if(change=="none")b.Done();else Assert.Single(b.Steps);
        }
    }
    [Theory][InlineData("51603")][InlineData("http")][InlineData("throw")]
    public async Task DetailQuota_FailuresAndRecreationCannotResetLane(string failure)
    {
        var clock=new ObservationTestClock();using var a=new OkxExecutionObservationHttpFixture(clock);using var b=new OkxExecutionObservationHttpFixture(clock);b.Scope=new("Demo",a.Scope.Uid,a.Scope.Uid,"USD","second");var fence=new DispatchFence();
        if(failure=="throw")a.Steps.Enqueue((DetailPath,_=>throw new HttpRequestException("PRIVATE_THROW")));else AddRaw(a,DetailPath,"{\"code\":\"51603\",\"msg\":\"PRIVATE_MSG\",\"data\":[]}",failure=="http"?503:200);
        await using(var old=Old(a,fence))Assert.Equal("Unknown",(await old.LookupAsync("BTC-USDC","987654","Ab9",default)).Reason);
        Add(b,DetailPath,Wire("order"));await using(var fresh=b.Client())Assert.Equal(OkxObservationStatus.Observed,(await fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default)).Status);
        Assert.True(b.Sent[0].Utc-a.Sent[0].Utc>=TimeSpan.FromMilliseconds(40));
        Add(a,DetailPath,OldControl());await using(var recreated=Old(a,fence))Assert.Equal("Observed",(await recreated.LookupAsync("BTC-USDC","987654","Ab9",default)).Reason);
        Assert.True(a.Sent[1].Utc-b.Sent[0].Utc>=TimeSpan.FromMilliseconds(40));a.Done();b.Done();
    }
    [Theory][InlineData("instrument")][InlineData("environment")][InlineData("cancel")][InlineData("history")]
    public async Task DetailQuota_IndependentCapacityIsNotBlocked(string separate)
    {
        var clock=new ObservationTestClock();using var a=new OkxExecutionObservationHttpFixture(clock);using var b=new OkxExecutionObservationHttpFixture(clock);b.Scope=new(separate=="environment"?"Live":"Demo",a.Scope.Uid,a.Scope.Uid,"USD","second");var fence=new DispatchFence();
        await using var first=Old(a,fence);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        a.Steps.Enqueue((DetailPath,async ct=>{await release.Task.WaitAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent(OldControl().ToJsonString())};}));var blocked=first.LookupAsync("BTC-USDC","987654","Ab9",default);Assert.Single(a.Sent);
        try
        {
            if(separate=="cancel")
            {
                using var handler=new CancelPeer();await using var cancel=OkxExecutionClient.ForTests(handler,clock,b.Scope,b,b.Fingerprint,fence,(t,ct)=>{clock.Advance(t);return Task.CompletedTask;});
                var r=await cancel.CancelAsync(OkxPreparedRequest.Cancel(b.Scope,"BTC-USDC","987654","Ab9",clock.Utc.AddSeconds(5)),default);Assert.Equal(OkxWriteDisposition.Acknowledged,r.Disposition);Assert.Equal(1,handler.Sends);
            }
            else
            {
                await using var fresh=b.Client();string instrument=separate=="instrument"?"ETH-USDC":"BTC-USDC";
                if(separate=="history"){Add(b,Path("history"),Rows("history"));Assert.Equal(OkxObservationStatus.Observed,(await fresh.ReadHistoryAsync(0,new(null,null,Begin,End,null),b.Context,default)).Status);}
                else{Add(b,DetailPath.Replace("BTC-USDC",instrument),Wire("order"));Assert.Equal(OkxObservationStatus.Observed,(await fresh.ReadOrderAsync(new(instrument,"987654","Ab9"),b.Context,default)).Status);}
                Assert.Single(b.Sent);b.Done();
            }
        }
        finally{release.TrySetResult();}
        Assert.Equal("Observed",(await blocked).Reason);a.Done();
    }
    private sealed class CancelPeer:HttpMessageHandler
    {
        internal int Sends;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Post,request.Method);Assert.Equal("/api/v5/trade/cancel-order",request.RequestUri!.PathAndQuery);Assert.Equal("{\"instId\":\"BTC-USDC\",\"ordId\":\"987654\",\"clOrdId\":\"Ab9\"}",await request.Content!.ReadAsStringAsync(ct));
            var stamp=request.Headers.GetValues("OK-ACCESS-TIMESTAMP").Single();var operand=stamp+"POST/api/v5/trade/cancel-order"+await request.Content.ReadAsStringAsync(ct);
            Assert.Equal(Convert.ToBase64String(System.Security.Cryptography.HMACSHA256.HashData(Encoding.ASCII.GetBytes(OkxExecutionObservationHttpFixture.Secret),Encoding.UTF8.GetBytes(operand))),request.Headers.GetValues("OK-ACCESS-SIGN").Single());Sends++;
            return new(HttpStatusCode.OK){Content=new StringContent("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ordId\":\"987654\",\"clOrdId\":\"Ab9\",\"ts\":\"1793448000000\",\"sCode\":\"0\",\"sMsg\":\"\"}]}")};
        }
    }
    [Fact] public async Task ReadRecovery_CanonicalDigestHandlesBoundedDecodedUnicode()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        // Valid wire below 1MiB; decoded U+2028 must be escaped by the canonical JSON encoder.
        string body="{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"billId\":\"1\",\"ccy\":\"EUR\",\"type\":\"\",\"subType\":\"\",\"notes\":\""+new string('\u2028',180000)+"\"}]}";
        Assert.True(Encoding.UTF8.GetByteCount(body)<1048576);AddRaw(f,Path("bills"),body);
        var r=await Read(c,f,"bills");Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Single(r.Rows);Assert.DoesNotContain(new string('\u2028',10),JsonSerializer.Serialize(r.Envelope));f.Done();
    }
    [Fact] public async Task ReadRecovery_CumulativeOrderIsNotFill()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var o=Wire("order");o["data"]![0]!["accFillSz"]="4";o["data"]![0]!["fillSz"]="1";o["data"]![0]!["fee"]="-0.4";Add(f,Path("order"),o);
        var parent=Assert.Single((await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,default)).Rows);Assert.Equal(4m,parent.AccFillSz.Value);Assert.Equal(1m,parent.FillSz.Value);Assert.Equal(-0.4m,parent.Fee.Value);
        var wire=Rows("fills","900","800");wire["data"]![0]!["fillSz"]="3";wire["data"]![1]!["fillSz"]="1";wire["data"]![0]!["fee"]="-0.3";wire["data"]![1]!["fee"]="-0.1";Add(f,Path("fills"),wire);Add(f,Path("bills"),Wire("bills"));
        var individual=await c.ReadFillsAsync(0,new(null,null,Begin,End,null),f.Context,default);Assert.Equal(new[]{3m,1m},individual.Rows.Select(x=>x.FillSz.Value!.Value));Assert.Equal(new[]{-0.3m,-0.1m},individual.Rows.Select(x=>x.Fee.Value!.Value));Assert.Single((await c.ReadBillsAsync(0,new(null,null,Begin,End,null),f.Context,default)).Rows);f.Done();
    }
    [Theory][InlineData("unknown")][InlineData("case")][InlineData("duplicate")][InlineData("null")][InlineData("attachment-null")][InlineData("linked-null")][InlineData("attachment33")][InlineData("depth33")][InlineData("utf8")][InlineData("body")][InlineData("surrogate")][InlineData("decimal-underflow")][InlineData("decimal-round")][InlineData("provider")]
    public async Task ReadRecovery_ResourceAndGrammarFailuresNeverPartiallyObserve(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var good=Wire("order",true);Add(f,Path("order"),good);Assert.Equal(OkxObservationStatus.Observed,(await Read(c,f,"order")).Status);
        var w=Wire("order",true);var row=w["data"]![0]!;string? raw=null;
        switch(change)
        {
            case "unknown":row["sCode"]="0";break;case "case":row["Fee"]="0";break;
            case "duplicate":raw=w.ToJsonString().Replace("\"code\":\"0\"","\"code\":\"0\",\"code\":\"0\"");break;
            case "null":row["fee"]=null;break;case "attachment-null":row["attachAlgoOrds"]=null;break;case "linked-null":row["linkedAlgoOrd"]=null;break;
            case "attachment33":var a=row["attachAlgoOrds"]!.AsArray();var one=a[0]!.DeepClone();a.Clear();for(int n=0;n<33;n++)a.Add(one.DeepClone());break;
            case "depth33":raw="{\"code\":\"0\",\"msg\":\"\",\"data\":"+new string('[',33)+"0"+new string(']',33)+"}";break;
            case "body":raw=new string('x',1048577);break;
            case "surrogate":row["attachAlgoOrds"]![0]!["failReason"]="SURROGATE";raw=w.ToJsonString().Replace("SURROGATE","\\uD800");break;
            case "decimal-underflow":row["fee"]="0.00000000000000000000000000001";break;case "decimal-round":row["fee"]="1.00000000000000000000000000001";break;
            case "provider":f.ProviderError=new Exception("PRIVATE_PROVIDER_SENTINEL");break;
        }
        CapturedStream? stream=null;
        if(change!="provider")
        {
            var bytes=change=="utf8"?new byte[]{123,34,255,34,125}:Encoding.UTF8.GetBytes(raw??w.ToJsonString());stream=new CapturedStream(bytes);
            f.Steps.Enqueue((Path("order"),_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)})));
        }
        var r=await Read(c,f,"order");Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);Assert.DoesNotContain("PRIVATE_",JsonSerializer.Serialize(r.Envelope)+r.Envelope);
        if(stream is not null){Assert.NotNull(stream.Buffer);Assert.Equal(1048577,stream.Buffer!.Length);Assert.All(stream.Buffer,b=>Assert.Equal((byte)0,b));}f.Done();
    }
    private sealed class CapturedStream(byte[] bytes):Stream
    {
        internal byte[]? Buffer;private int offset;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override ValueTask<int> ReadAsync(Memory<byte> memory,CancellationToken ct=default)
        {
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)memory,out var segment));if(Buffer is not null)Assert.Same(Buffer,segment.Array);Buffer=segment.Array;
            int n=Math.Min(Math.Min(memory.Length,4096),bytes.Length-offset);bytes.AsMemory(offset,n).CopyTo(memory);offset+=n;return ValueTask.FromResult(n);
        }
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long n)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
    [Fact] public async Task Collect_ReserveFinalBookendAndCountEveryOccurrence()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();string[] Ids(int page)=>Enumerable.Range(0,100).Select(n=>(10000-page*100-n).ToString()).ToArray();var head=Rows("pending",Ids(0));Add(f,Path("pending"),head);
        for(int p=1;p<18;p++)Add(f,After("pending",(10000-(p-1)*100-99).ToString()),Rows("pending",Ids(p)));
        Add(f,After("pending","8201"),Rows("pending"));Add(f,Path("pending"),head);
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"pending"),default);Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Equal(20,f.Sent.Count);Assert.Equal(20,r.Trace.Count);Assert.Equal(1900,r.Trace.Sum(x=>x.RowCount));Assert.Equal(1800,r.Orders.Count);Assert.True(r.Trace[^1].IsBookend);f.Done();
    }
    [Theory][InlineData("none")][InlineData("utc")][InlineData("mono")][InlineData("revoke")]
    public async Task DetailQuota_FinalSigningCheckpointIsReached(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();bool opened=false;int post=0;var samples=new List<DateTimeOffset>();DateTimeOffset start=f.Clock.Utc;long mono=f.Clock.Mono;int checkpoints=0;
        f.Clock.OnUtcRead=()=>{f.Clock.Utc+=TimeSpan.FromMilliseconds(1);if(opened&&post==1)samples.Add(f.Clock.Utc);};
        f.OnOpen=()=>{opened=true;post=0;samples.Clear();};f.OnFence=()=>{if(!opened)return;if(++post!=2)return;checkpoints++;Assert.Equal(4,samples.Count);Assert.True(f.Allowed);Assert.Equal(mono,f.Clock.Mono);Assert.InRange(f.Clock.Utc-start,TimeSpan.Zero,TimeSpan.FromSeconds(1));if(change=="utc")f.Clock.Utc=start+TimeSpan.FromSeconds(10);if(change=="mono")f.Clock.Mono=mono+TimeSpan.FromSeconds(10).Ticks;if(change=="revoke")f.Allowed=false;};
        f.ExpectedSigningUtc=()=>samples[1];Add(f,DetailPath,Wire("order"));var r=await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,default);
        Assert.Equal(1,checkpoints);Assert.Equal(1,f.Opens);Assert.Equal(change=="none"?1:0,f.Sent.Count);Assert.Equal(change=="none"?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);if(change=="none")f.Done();else Assert.Single(f.Steps);
    }
    [Theory][InlineData("none")][InlineData("utc")][InlineData("revoke")]
    public async Task DetailQuota_OldFinalFenceRemainsAfterSigning(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();var fence=new DispatchFence();await using var c=Old(f,fence);Add(f,DetailPath,OldControl());Assert.Equal("Observed",(await c.LookupAsync("BTC-USDC","987654","Ab9",default)).Reason);f.Clock.Advance(TimeSpan.FromMilliseconds(40));var start=f.Clock.Utc;
        fence.OnCheck=()=>{Assert.Equal(2,f.Opens);Assert.Equal(2,fence.Checks);if(change=="utc")f.Clock.Utc=start.AddSeconds(10);if(change=="revoke")fence.Allowed=false;};Add(f,DetailPath,OldControl());var r=await c.LookupAsync("BTC-USDC","987654","Ab9",default);
        Assert.Equal(2,f.Opens);Assert.Equal(2,fence.Checks);Assert.Equal(change=="none"?"Observed":"Unknown",r.Reason);Assert.Equal(change=="none"?2:1,f.Sent.Count);if(change=="none")f.Done();else Assert.Single(f.Steps);
    }
    [Theory][InlineData(true,"revoke")][InlineData(false,"revoke")][InlineData(true,"expire")][InlineData(false,"expire")][InlineData(true,"dispose")][InlineData(false,"dispose")]
    public async Task DetailQuota_QueuedWaitChecksOriginalLifetime(bool oldFirst,string change)
    {
        var clock=new ObservationTestClock();using var a=new OkxExecutionObservationHttpFixture(clock);using var b=new OkxExecutionObservationHttpFixture(clock);b.Scope=new("Demo",a.Scope.Uid,a.Scope.Uid,"USD","second");var fence=new DispatchFence();await using var old=Old(a,fence);await using var fresh=b.Client();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first=oldFirst?a:b;var second=oldFirst?b:a;first.Steps.Enqueue((DetailPath,async ct=>{await release.Task.WaitAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent((oldFirst?OldControl():Wire("order")).ToJsonString())};}));Add(second,DetailPath,oldFirst?Wire("order"):OldControl());
        Task<OkxOrderObservation> lookup;Task<OkxObservationRead<OkxExecutionOrderDto>> detail;
        if(oldFirst){lookup=old.LookupAsync("BTC-USDC","987654","Ab9",default);detail=fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default);}else{detail=fresh.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),b.Context,default);lookup=old.LookupAsync("BTC-USDC","987654","Ab9",default);}
        Assert.Single(first.Sent);Assert.Empty(second.Sent);Assert.Equal(0,second.Opens);Task? disposing=null;
        if(change=="revoke"){if(oldFirst)b.Allowed=false;else fence.Allowed=false;}if(change=="expire")clock.Advance(TimeSpan.FromSeconds(10));if(change=="dispose")disposing=oldFirst?fresh.DisposeAsync().AsTask():old.DisposeAsync().AsTask();
        release.TrySetResult();var o=await lookup;var n=await detail;if(disposing is not null)await disposing;Assert.Empty(second.Sent);if(oldFirst)Assert.Equal(OkxObservationStatus.Failed,n.Status);else Assert.Equal("Unknown",o.Reason);Assert.Single(second.Steps);
        if(oldFirst)Assert.Equal(0,b.Opens); // revoked while waiting: recheck before credential open
        else Assert.Equal(change=="revoke"?1:0,a.Opens); // legacy final fence is intentionally after provider/signing
    }
    [Fact] public async Task ReadRecovery_PayloadDigestIsCanonicalDecodedAndPresenceSensitive()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();string[] bodies={"{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"ccy\":\"EUR\",\"billId\":\"1\",\"type\":\"\",\"subType\":\"\"}]}","{\"data\":[{\"type\":\"\",\"subType\":\"\",\"billId\":\"1\",\"ccy\":\"E\\u0055R\"}],\"msg\":\"\",\"code\":\"0\"}"};
        var expected=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("{\"billId\":\"1\",\"ccy\":\"EUR\",\"subType\":\"\",\"type\":\"\"}"))).ToLowerInvariant();
        foreach(var body in bodies){AddRaw(f,Path("bills"),body);var r=await c.ReadBillsAsync(0,new(null,null,Begin,End,null),f.Context,default);Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Equal(expected,Assert.Single(r.Rows).PayloadDigest);}f.Done();
    }
    [Fact] public async Task Collect_OutsideSourceTimeIsRetainedAndDiagnosed()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var head=Rows("history","900");Add(f,Path("history"),head);Add(f,After("history","900"),Rows("history"));Add(f,Path("history"),head);var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"history"),default);
        Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Single(r.Orders);Assert.Contains("OutsideRequestedTimeBounds",r.Limitations);Assert.Equal("1708587373361",r.Orders[0].CTime.Raw);f.Done();
    }
    [Theory][InlineData("none")][InlineData("utc")][InlineData("mono")]
    public async Task Collect_TotalBudgetIsRecheckedInsideNextReadProvider(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        var origin=f.Clock.Utc;
        f.OnOpen=()=>{if(f.Opens<=6)f.Clock.Advance(TimeSpan.FromSeconds(8));else if(f.Opens==7){f.Clock.Utc=origin.AddSeconds(58);f.Clock.Mono=TimeSpan.FromSeconds(58).Ticks;}else if(f.Opens==8){if(change=="utc")f.Clock.Utc+=TimeSpan.FromSeconds(2);if(change=="mono")f.Clock.Mono+=TimeSpan.FromSeconds(2).Ticks;}};
        Add(f,Path("pending"),Rows("pending","900"));for(int n=900;n>894;n--)Add(f,After("pending",n.ToString()),Rows("pending",(n-1).ToString()));Add(f,After("pending","894"),Rows("pending"));Add(f,Path("pending"),Rows("pending","900"));
        var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,"pending"),default);
        Assert.Equal(change=="none"?9:7,f.Sent.Count);Assert.Equal(change=="none"?OkxObservationCollectionStatus.Collected:OkxObservationCollectionStatus.Incomplete,r.Status);Assert.Equal(7,r.Orders.Count);Assert.Equal(change=="none"?9:8,f.Opens);
        if(change=="none")f.Done();else{Assert.Equal(2,f.Steps.Count);Assert.Equal(OkxObservationStatus.Failed,r.Trace[^1].Status);Assert.False(r.Trace[^1].IsLocalStop);Assert.Null(r.Trace[^1].ResponseDigest);}
    }
    [Fact] public async Task Collect_ExpiredLookupAndEmptyListsNeverConferAuthority()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();AddRaw(f,DetailPath,"{\"code\":\"51603\",\"msg\":\"PRIVATE_SENTINEL\",\"data\":[]}");var lookup=await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,default);Assert.Equal(OkxObservationStatus.Failed,lookup.Status);Assert.Empty(lookup.Rows);
        foreach(var name in new[]{"pending","history","historyArchive"}){Add(f,Path(name),Rows(name));Add(f,Path(name),Rows(name));var r=await new OkxObservationPager(c).CollectAsync(CollectionQuery(f,name),default);Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Empty(r.Orders);Assert.Contains("RetentionNotEstablished",r.Limitations);Assert.Contains("CollectionNotFinancialAuthority",r.Limitations);}Assert.Equal(7,f.Sent.Count);f.Done();
    }
    [Fact] public async Task ReadRecovery_Attachment32IsLocalResourceCapNotCashAdmission()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();var w=Wire("order",true);var a=w["data"]![0]!["attachAlgoOrds"]!.AsArray();var row=a[0]!.DeepClone();a.Clear();for(int n=0;n<32;n++)a.Add(row.DeepClone());Add(f,DetailPath,w);
        var r=await c.ReadOrderAsync(new("BTC-USDC","987654","Ab9"),f.Context,default);Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Equal(32,Assert.Single(r.Rows).AttachAlgoOrds!.Value.Length);Assert.Equal(-1m,r.Rows[0].AttachAlgoOrds!.Value[0].TpOrdPx.Value);f.Done();
    }
    [Fact] public async Task DetailQuota_RealHistoryHoldDoesNotOccupyCancelLane()
    {
        var clock=new ObservationTestClock();using var historyPeer=new OkxExecutionObservationHttpFixture(clock);using var cancelPeer=new OkxExecutionObservationHttpFixture(clock);cancelPeer.Scope=new("Demo",historyPeer.Scope.Uid,historyPeer.Scope.Uid,"USD","cancel-binding");await using var history=historyPeer.Client();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        historyPeer.Steps.Enqueue((Path("history"),async ct=>{await release.Task.WaitAsync(ct);return new(HttpStatusCode.OK){Content=new StringContent(Rows("history").ToJsonString())};}));var waiting=history.ReadHistoryAsync(0,new(null,null,Begin,End,null),historyPeer.Context,default);Assert.Single(historyPeer.Sent);
        try{using var handler=new CancelPeer();await using var cancel=OkxExecutionClient.ForTests(handler,clock,cancelPeer.Scope,cancelPeer,cancelPeer.Fingerprint,new DispatchFence(),(t,ct)=>{clock.Advance(t);return Task.CompletedTask;});var r=await cancel.CancelAsync(OkxPreparedRequest.Cancel(cancelPeer.Scope,"BTC-USDC","987654","Ab9",clock.Utc.AddSeconds(5)),default);Assert.Equal(OkxWriteDisposition.Acknowledged,r.Disposition);Assert.Equal(1,handler.Sends);Assert.False(waiting.IsCompleted);}
        finally{release.TrySetResult();}
        Assert.Equal(OkxObservationStatus.Observed,(await waiting).Status);historyPeer.Done();
    }
}
