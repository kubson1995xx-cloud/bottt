using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxExecutionAlgoObservationTests
{
    internal static JsonNode Oracle()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/stream-algo-schema.json")))dir=dir.Parent;
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/stream-algo-schema.json")))!;
    }
    internal static JsonNode Control(string name)=>Oracle()["endpoints"]![name]!["control"]!.DeepClone();
    internal static OkxObservationAlgoPageQuery Page(bool history=false,int limit=100,OkxObservationAlgoCursor? cursor=null)=>new(history?OkxAlgoObservationSurface.History:OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.Conditional,history?OkxAlgoHistoryState.Effective:null,null,null,cursor,limit);
    internal static void Add(OkxExecutionObservationHttpFixture f,string path,JsonNode body,int status=200)=>f.Steps.Enqueue((path,_=>Task.FromResult(new HttpResponseMessage((HttpStatusCode)status){Content=new StringContent(body.ToJsonString(),Encoding.UTF8,"application/json")})));
    private static Task<OkxObservationRead<OkxExecutionAlgoDto>> Read(OkxExecutionObservationClient c,OkxExecutionObservationHttpFixture f,string name)=>name switch {"detail"=>c.ReadAlgoAsync(new("123",null),f.Context,default),"pending"=>c.ReadPendingAlgosAsync(Page(),f.Context,default),_=>c.ReadAlgoHistoryAsync(Page(true),f.Context,default)};
    private static string PathFor(string name)=>name switch {"detail"=>"/api/v5/trade/order-algo?algoId=123","pending"=>"/api/v5/trade/orders-algo-pending?instType=SPOT&limit=100&ordType=conditional",_=>"/api/v5/trade/orders-algo-history?instType=SPOT&limit=100&ordType=conditional&state=effective"};
    [Theory][InlineData("detail","Demo")][InlineData("detail","Live")][InlineData("pending","Demo")][InlineData("pending","Live")][InlineData("history","Demo")][InlineData("history","Live")]
    public async Task AlgoRead_PopulatedOriginalEndpoint(string name,string environment)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Scope=new(environment,f.Scope.Uid,f.Scope.MainUid,"USD","algo-original");var body=Control(name);Add(f,PathFor(name),body);
        await using var c=f.Client();var r=await Read(c,f,name);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);var original=body["data"]![0]!;
        Assert.Equal(original["algoId"]!.GetValue<string>(),row.AlgoId);Assert.Equal(original["instId"]!.GetValue<string>(),row.InstId);Assert.Equal(original["state"]!.GetValue<string>(),row.State);
        Assert.Equal(original["triggerTime"]!.GetValue<string>(),row.TriggerTime.Raw);Assert.Equal(original["tradeQuoteCcy"]!.GetValue<string>(),row.TradeQuoteCcy);Assert.Equal("",row.ClOrdId);Assert.Equal(original["ordIdList"]!.AsArray().Select(x=>x!.GetValue<string>()),row.OrdIdList!.Value);
        if(name=="history"){Assert.Equal("SWAP",row.InstType);Assert.Equal("BTC-USDT-SWAP",row.InstId);Assert.Equal("1",row.FailCode);}
        Assert.DoesNotContain(typeof(OkxExecutionAlgoDto).GetProperties(),p=>p.Name is "Ready" or "Protected" or "Accepted");f.Done();
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public async Task AlgoRead_ExactLiteralQueryAndSignature(int vector)
    {
        using var f=new OkxExecutionObservationHttpFixture();var v=Oracle()["queryVectors"]![vector]!;var path=v["path"]!.GetValue<string>();Add(f,path,Control(vector==0?"detail":vector==1?"pending":"history"));
        await using var c=f.Client();OkxObservationRead<OkxExecutionAlgoDto> r;
        if(vector==0)r=await c.ReadAlgoAsync(new("123","Ab9"),f.Context,default);
        else if(vector==1)r=await c.ReadPendingAlgosAsync(new(OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.ConditionalAndOco,null,null,"BTC-USDC",new(OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.ConditionalAndOco,null,null,"BTC-USDC",OkxObservationCursorDirection.After,"900"),2),f.Context,default);
        else r=await c.ReadAlgoHistoryAsync(new(OkxAlgoObservationSurface.History,OkxAlgoOrderFilter.Trigger,OkxAlgoHistoryState.Effective,"123","BTC-USDC",new(OkxAlgoObservationSurface.History,OkxAlgoOrderFilter.Trigger,OkxAlgoHistoryState.Effective,"123","BTC-USDC",OkxObservationCursorDirection.Before,"100"),1),f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Equal(path,r.Endpoint+r.ExactQuery);Assert.NotEqual("Ab9",Assert.Single(r.Rows).AlgoClOrdId);f.Done();
    }
    [Fact] public async Task AlgoRead_QueryRefusalsAreLocal()
    {
        Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoQuery(null,null));Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoQuery("123",""));Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoQuery("01/2",null));
        Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoPageQuery(OkxAlgoObservationSurface.History,OkxAlgoOrderFilter.Oco,null,null,null,null));
        Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoPageQuery(OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.Oco,OkxAlgoHistoryState.Canceled,null,null,null));
        Assert.Throws<ArgumentException>(()=>new OkxObservationAlgoPageQuery(OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.Oco,null,null,"BTC-USDC",new(OkxAlgoObservationSurface.Pending,OkxAlgoOrderFilter.Oco,null,null,null,OkxObservationCursorDirection.After,"12")));
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();Assert.Equal("InvalidObservationRequest",(await c.ReadPendingAlgosAsync(Page(true),f.Context,default)).ErrorCode);Assert.Equal("InvalidObservationRequest",(await c.ReadAlgoHistoryAsync(Page(),f.Context,default)).ErrorCode);Assert.Equal(0,f.Opens);Assert.Empty(f.Sent);
    }
    [Theory][InlineData("detail")][InlineData("pending")][InlineData("history")]
    public async Task AlgoRead_EndpointSpecificNestedTypes(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();var body=Control(name);var row=body["data"]![0]!;
        row["subAlgoIdList"]=new JsonArray("13","");row["advChaseParams"]=JsonNode.Parse("[{\"chaseType\":\"distance\",\"chaseVal\":\"0\",\"maxChaseType\":\"\",\"maxChaseVal\":\"\"}]");
        row["attachAlgoOrds"]=JsonNode.Parse("[{\"attachAlgoClOrdId\":\"manual!\",\"tpOrdPx\":\"-1\",\"slTriggerRatio\":\"0\"}]");row["linkedOrd"]=JsonNode.Parse("{\"ordId\":\"\"}");row["isTradeBorrowMode"]="false";
        if(name!="detail"){row["lmtOrderNumber"]="2";row["aggressiveness"]="radical";row["triggerParams"]=JsonNode.Parse("[{\"triggerAction\":\"start\",\"triggerStrategy\":\"rsi\",\"thold\":\"50\",\"timePeriod\":\"14\"}]");}
        Add(f,PathFor(name),body);await using var c=f.Client();var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Observed,r.Status);var a=Assert.Single(r.Rows);Assert.Equal(new[]{"13",""},a.SubAlgoIdList!.Value);Assert.Equal(-1m,a.AttachAlgoOrds!.Value[0].TpOrdPx.Value);Assert.Equal("",a.LinkedOrd!.OrdId);Assert.Equal("false",a.IsTradeBorrowMode);f.Done();
    }
    [Fact] public async Task AlgoRead_AbsenceEmptyZeroAndForeignIdentity()
    {
        using var f=new OkxExecutionObservationHttpFixture();var body=JsonNode.Parse("{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"instType\":\"FUTURES\",\"instId\":\"OLD-USD-OBLIGATION\",\"algoId\":\"999\",\"algoClOrdId\":\"manual!foreign\",\"actualSz\":\"0\",\"ordPx\":\"-1\",\"tpOrdPx\":\"\",\"state\":\"unknown\",\"tradeQuoteCcy\":\"USDT\"}]}")!;
        Add(f,PathFor("detail"),body);await using var c=f.Client();var r=await Read(c,f,"detail");Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);Assert.Null(row.CTime.Raw);Assert.Null(row.CTime.Utc);Assert.Equal("",row.TpOrdPx.Raw);Assert.Null(row.TpOrdPx.Value);Assert.Equal(0m,row.ActualSz.Value);Assert.Equal(-1m,row.OrdPx.Value);Assert.Null(row.OrdIdList);Assert.Equal("manual!foreign",row.AlgoClOrdId);f.Done();
    }
    internal static JsonNode Rows(params (string Id,string Tag)[] rows)
    {
        var body=Control("pending");var template=body["data"]![0]!.DeepClone();body["data"]=new JsonArray();
        foreach(var (id,tag) in rows){var row=template.DeepClone();row["algoId"]=id;row["tag"]=tag;row["ordIdList"]=new JsonArray("999999");body["data"]!.AsArray().Add(row);}return body;
    }
    [Fact] public async Task AlgoCollect_ShortPageNeedsExplicitEmptyAndHead()
    {
        using var f=new OkxExecutionObservationHttpFixture();var head=Rows(("900","head"));
        Add(f,PathFor("pending"),head);Add(f,"/api/v5/trade/orders-algo-pending?after=900&instType=SPOT&limit=100&ordType=conditional",Rows(("800","tail")));
        Add(f,"/api/v5/trade/orders-algo-pending?after=800&instType=SPOT&limit=100&ordType=conditional",Rows());Add(f,PathFor("pending"),head);
        await using var c=f.Client();var r=await new OkxObservationPager(c).CollectAlgosAsync(Page(),f.Context,default);
        Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);Assert.Equal(new[]{"900","800"},r.Rows.Select(x=>x.AlgoId));Assert.Equal(4,r.Trace.Length);Assert.Equal("900",r.Trace[1].RequestedCursor!.Value);Assert.True(r.Trace[2].EmptyTerminal);Assert.True(r.Trace[3].IsBookend);Assert.Contains("CollectionNotFinancialAuthority",r.Limitations);f.Done();
    }
    [Fact] public async Task AlgoCollect_NonCursorConflictSurvivesEqualHeads()
    {
        using var f=new OkxExecutionObservationHttpFixture();Add(f,PathFor("pending"),Rows(("900","old"),("800","cursor")));
        Add(f,"/api/v5/trade/orders-algo-pending?after=800&instType=SPOT&limit=100&ordType=conditional",Rows(("900","changed"),("700","tail")));
        await using var c=f.Client();var r=await new OkxObservationPager(c).CollectAlgosAsync(Page(),f.Context,default);
        Assert.Equal(OkxObservationCollectionStatus.Conflict,r.Status);Assert.Equal(4,r.Rows.Length);Assert.Equal(2,r.Rows.Count(x=>x.AlgoId=="900"));f.Done();
    }
    [Theory][InlineData("empty")][InlineData("overlap")][InlineData("moving")][InlineData("cycle")][InlineData("order")][InlineData("max")]
    public async Task AlgoCollect_Boundaries(string variant)
    {
        using var f=new OkxExecutionObservationHttpFixture();var endpoint=PathFor("pending");string After(string id)=>"/api/v5/trade/orders-algo-pending?after="+id+"&instType=SPOT&limit=100&ordType=conditional";
        if(variant=="empty"){Add(f,endpoint,Rows());Add(f,endpoint,Rows());}
        else if(variant=="max")for(int i=0;i<19;i++){var ids=Enumerable.Range(0,100).Select(n=>((10000-i*100-n).ToString(),"")).ToArray();Add(f,i==0?endpoint:After((10000-(i-1)*100-99).ToString()),Rows(ids));}
        else {Add(f,endpoint,Rows(("900",""),("800","")));if(variant=="order")Add(f,After("800"),Rows(("700",""),("750","")));else if(variant=="cycle")Add(f,After("800"),Rows(("900",""),("800","")));else {Add(f,After("800"),Rows(("800",""),("700","")));Add(f,After("700"),Rows());Add(f,endpoint,variant=="moving"?Rows(("900","changed"),("800","")):Rows(("900",""),("800","")));}}
        await using var c=f.Client();var r=await new OkxObservationPager(c).CollectAlgosAsync(Page(),f.Context,default);
        Assert.Equal(variant is "empty" or "overlap"?OkxObservationCollectionStatus.Collected:variant=="moving"?OkxObservationCollectionStatus.Conflict:OkxObservationCollectionStatus.Incomplete,r.Status);
        if(variant=="max"){Assert.Equal(1900,r.Rows.Length);Assert.Equal(20,r.Trace.Length);Assert.True(r.Trace[^1].IsLocalStop);Assert.Equal("PageLimit",r.ErrorCode);}f.Done();
    }
    [Theory][InlineData("utc")][InlineData("mono")][InlineData("rollback")][InlineData("valid")]
    public async Task AlgoCollect_LaterProviderCannotExtendOriginalBudget(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();Add(f,PathFor("pending"),Rows(("900","")));Add(f,"/api/v5/trade/orders-algo-pending?after=900&instType=SPOT&limit=100&ordType=conditional",Rows());Add(f,PathFor("pending"),Rows(("900","")));
        f.OnOpen=()=>{if(f.Opens==2){if(change=="utc")f.Clock.Utc+=TimeSpan.FromSeconds(60);else if(change=="mono")f.Clock.Mono+=TimeSpan.FromSeconds(60).Ticks;else if(change=="rollback")f.Clock.Utc-=TimeSpan.FromSeconds(1);}};
        await using var c=f.Client();var r=await new OkxObservationPager(c).CollectAlgosAsync(Page(),f.Context,default);
        if(change=="valid"){Assert.Equal(OkxObservationCollectionStatus.Collected,r.Status);f.Done();}else {Assert.Equal(OkxObservationCollectionStatus.Incomplete,r.Status);Assert.Single(f.Sent);Assert.Single(r.Rows);Assert.Equal(2,f.Steps.Count);}
    }
    public static IEnumerable<object[]> BadRows()
    {
        foreach(var name in new[]{"detail","pending","history"})
        foreach(var (field,value) in new[]{("reduceOnly","false"),("ordIdList","[{}]"),("subAlgoIdList","[1]"),("advChaseParams","[{\"chaseVal\":0}]"),("attachAlgoOrds","[{\"failCode\":\"0\"}]"),("linkedOrd","{\"ordId\":null}"),("extra","\"x\""),("InstId","\"BTC-USDC\""),("instId","\"\""),("algoId","null"),("lever","1")})
        {var b=Control(name);b["data"]![0]![field]=JsonNode.Parse(value);yield return new object[]{name,b.ToJsonString()};}
        var d=Control("detail");d["data"]![0]!["triggerParams"]=new JsonArray();yield return new object[]{"detail",d.ToJsonString()};
        foreach(var name in new[]{"detail","pending","history"}){var b=Control(name);b["data"]![0]!["lever"]="LONE";yield return new object[]{name,b.ToJsonString().Replace("LONE","\\uD800")};}
    }
    [Theory][MemberData(nameof(BadRows))] public async Task AlgoRead_MalformedRowsNeverPartial(string name,string body)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Steps.Enqueue((PathFor(name),_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)})));await using var c=f.Client();var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);Assert.Single(f.Sent);f.Done();
    }
}
