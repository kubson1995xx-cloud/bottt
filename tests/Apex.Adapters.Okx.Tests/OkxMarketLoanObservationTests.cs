using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxMarketLoanObservationTests
{
    internal const string Endpoint="/api/v5/account/interest-limits", Query="?type=2";
    private static string Official()
    {
        var dir=new DirectoryInfo(AppContext.BaseDirectory);
        while(dir is not null&&!File.Exists(Path.Combine(dir.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/market-loans-official.json")))dir=dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName,"tests/Apex.Adapters.Okx.Tests/Fixtures/ExecutionObservations/market-loans-official.json"));
    }
    // Reflection deliberately permits a real old-DLL behavioral RED, not a compiler error.
    private static async Task<object> Read(OkxExecutionObservationClient c,OkxExecutionObservationHttpFixture f,CancellationToken ct=default)
    {
        var m=typeof(OkxExecutionObservationClient).GetMethod("ReadMarketLoansAsync");
        Assert.True(m is not null,"MISSING_TYPED_MARKET_LOAN_GET");
        Assert.Equal(new[]{typeof(OkxObservationContext),typeof(CancellationToken)},m!.GetParameters().Select(p=>p.ParameterType));
        var task=(Task)m.Invoke(c,new object[]{f.Context,ct})!;await task;return task.GetType().GetProperty("Result")!.GetValue(task)!;
    }
    private static JsonElement Element(object r)=>JsonSerializer.SerializeToElement(r);
    private static void Add(OkxExecutionObservationHttpFixture f,string body,int status=200)=>f.Steps.Enqueue((Endpoint+Query,_=>Task.FromResult(new HttpResponseMessage((System.Net.HttpStatusCode)status){Content=new StringContent(body)})));
    [Fact] public async Task Official_sample_signed_exact_GET_is_structure_not_clearance()
    {
        using var f=new OkxExecutionObservationHttpFixture();Add(f,Official());await using var c=f.Client();
        var r=Element(await Read(c,f));Assert.Equal(0,r.GetProperty("Status").GetInt32());Assert.Equal(Query,r.GetProperty("ExactQuery").GetString());
        var row=r.GetProperty("Rows")[0];Assert.Equal("0.85893159114900247077000000000000",row.GetProperty("DebtUsd").GetProperty("Raw").GetString());
        Assert.True(row.GetProperty("DebtUsd").GetProperty("Value").GetDecimal()>0);var b=row.GetProperty("Records")[0];
        Assert.Equal(0,b.GetProperty("UsedLimit").GetProperty("Value").GetDecimal());Assert.Equal("",b.GetProperty("InterestFreeLiability").GetProperty("Raw").GetString());
        Assert.Equal(JsonValueKind.Null,b.GetProperty("PotentialBorrowingAmount").GetProperty("Value").ValueKind);
        Assert.DoesNotContain(row.EnumerateObject(),p=>p.Name is "Ready" or "Clearance" or "Authority");f.Done();
    }
    public static IEnumerable<object[]> Mutations()
    {
        foreach(string kind in new[]{"wrong-type","negative","underflow","duplicate","duplicate-currency","empty-data","multiple-data","record-cap","nested-wrong-type","unknown"})yield return new object[]{kind};
    }
    [Theory,MemberData(nameof(Mutations))] public async Task Strict_shape_and_exact_money_refuse(string kind)
    {
        var w=JsonNode.Parse(Official())!;var row=w["data"]![0]!;var record=row["records"]![0]!;
        switch(kind){case "wrong-type":record["usedLmt"]=0;break;case "negative":record["usedLmt"]="-1";break;case "underflow":record["usedLmt"]="0.00000000000000000000000000001";break;
        case "duplicate-currency":row["records"]!.AsArray().Add(record.DeepClone());break;case "empty-data":w["data"]=new JsonArray();break;case "multiple-data":w["data"]!.AsArray().Add(row.DeepClone());break;
        case "record-cap":for(int i=1;i<257;i++)row["records"]!.AsArray().Add(record.DeepClone());break;case "nested-wrong-type":record["surplusLmtDetails"]!["allAcctRemainingQuota"]=false;break;case "unknown":record["newUnknown"]="PRIVATE_TEXT";break;}
        string body=w.ToJsonString();if(kind=="duplicate")body=body.Replace("\"ccy\":\"BTC\"","\"ccy\":\"BTC\",\"Ccy\":\"BTC\"");
        using var f=new OkxExecutionObservationHttpFixture();Add(f,body);await using var c=f.Client();var r=Element(await Read(c,f));Assert.Equal(1,r.GetProperty("Status").GetInt32());Assert.Empty(r.GetProperty("Rows").EnumerateArray());f.Done();
    }
    [Theory][InlineData("absent")][InlineData("blank")][InlineData("zero")][InlineData("positive")][InlineData("tiny")][InlineData("empty-records")]
    public async Task Unavailable_zero_positive_and_empty_records_remain_distinct(string kind)
    {
        var w=JsonNode.Parse(Official())!;var r=w["data"]![0]!["records"]![0]!;
        if(kind=="absent")r.AsObject().Remove("interestFreeLiab");else r["interestFreeLiab"]=kind switch{"blank"=>"","zero"=>"0","tiny"=>"0.0000000000000000000000000001",_=>"2.5"};
        if(kind=="empty-records")w["data"]![0]!["records"]=new JsonArray();
        using var f=new OkxExecutionObservationHttpFixture();Add(f,w.ToJsonString());await using var c=f.Client();var e=Element(await Read(c,f));Assert.Equal(0,e.GetProperty("Status").GetInt32());
        var records=e.GetProperty("Rows")[0].GetProperty("Records");if(kind=="empty-records")Assert.Empty(records.EnumerateArray());else Assert.Equal(kind is "absent" or "blank",records[0].GetProperty("InterestFreeLiability").GetProperty("Value").ValueKind==JsonValueKind.Null);f.Done();
    }
    [Theory][InlineData(503,"0")][InlineData(200,"50011")][InlineData(200,"51000")]
    public async Task Failed_endpoint_has_no_retry_no_rows_and_sanitized_status(int status,string code)
    {
        using var f=new OkxExecutionObservationHttpFixture();Add(f,"{\"code\":\""+code+"\",\"msg\":\"PRIVATE_TEXT\",\"data\":[]}",status);await using var c=f.Client();var r=Element(await Read(c,f));Assert.Equal(1,r.GetProperty("Status").GetInt32());Assert.Single(f.Sent);Assert.DoesNotContain("PRIVATE_TEXT",r.ToString());Assert.Equal(status,Assert.Single(c.ReadDiagnostics()).HttpStatus);f.Done();
    }
    [Theory][InlineData("none")][InlineData("utc")][InlineData("mono")][InlineData("stop")][InlineData("cancel")][InlineData("binding")]
    public async Task Shared_quota_and_provider_final_fences(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("account");await using var c=f.Client();Assert.Equal(OkxObservationStatus.Observed,(await c.ReadAccountAsync(f.Context,default)).Status);f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        using var ct=new CancellationTokenSource();f.OnOpen=()=>{if(change=="utc")f.Clock.Utc+=TimeSpan.FromSeconds(10);if(change=="mono")f.Clock.Mono+=TimeSpan.FromSeconds(10).Ticks;if(change=="stop")f.Allowed=false;if(change=="cancel")ct.Cancel();};
        if(change=="binding")f.MaterialScope=new("Demo","899999","899999","USD","other");if(change=="none")Add(f,Official());
        var r=Element(await Read(c,f,ct.Token));Assert.Equal(change=="none"?0:1,r.GetProperty("Status").GetInt32());Assert.Equal(change=="none"?2:1,f.Sent.Count);f.Done();
    }
}
