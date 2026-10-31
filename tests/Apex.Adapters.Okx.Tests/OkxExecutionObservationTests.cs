using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;

public sealed class OkxExecutionObservationTests
{
    [Theory][InlineData("Demo")][InlineData("Live")]
    public async Task ReadAccount_PopulatedOfficialSchemaPreservesObservedIdentity(string environment)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Scope=new(environment,"123456","123456","USD","observed-"+environment);
        f.Add("account");await using var c=f.Client();Assert.Equal(0,f.Opens);Assert.Empty(f.Sent);
        var r=await c.ReadAccountAsync(f.Context,default);Assert.Equal(OkxObservationStatus.Observed,r.Status);
        var row=Assert.Single(r.Rows);Assert.Equal("654321",row.Uid);Assert.Equal("654321",row.MainUid);Assert.Equal("read_only,trade",row.Permissions);
        Assert.False(row.AutoLoan);Assert.False(row.EnableSpotBorrow);Assert.False(row.SpotBorrowAutoRepay);Assert.Equal(f.Scope,r.RequestedScope);
        Assert.Equal(f.Context,r.Context);Assert.Equal(1,f.Opens);Assert.Single(f.Sent);f.Done();
    }
    [Fact] public async Task ReadBalance_RetainsAllCurrenciesAndUnknownApplicableFields()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("balance");await using var c=f.Client();var r=await c.ReadBalancesAsync(f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);var rows=Assert.Single(r.Rows).Details;
        Assert.Equal(new[]{"USD","BTC"},rows.Select(x=>x.Currency));Assert.Equal("",rows[0].Liability.Raw);Assert.Null(rows[0].Liability.Value);
        Assert.Null(rows[0].OrderFrozen.Value);Assert.Equal(0.1m,rows[1].Cash.Value);Assert.NotEqual(55837.43556134779m,rows[0].Cash.Value);f.Done();
    }
    [Fact] public async Task ReadInstruments_PreservesActualQuoteAndMaxUnits()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("instruments");await using var c=f.Client();var r=await c.ReadInstrumentsAsync(f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);Assert.Equal("BTC-USDC",row.Instrument);Assert.Equal("USDC",row.CatalogQuote);
        Assert.Equal(new[]{"USD","USDC"},row.TradeQuoteCurrencies);Assert.Equal("USDT",row.MaxValues["maxMktSz"].Unit);Assert.Equal("USD",row.MaxValues["maxLmtAmt"].Unit);
        Assert.Equal("1000000",row.MaxValues["maxMktSz"].Amount.Raw);Assert.Equal("base currency",row.MaxValues["maxLmtSz"].Unit);f.Done();
    }
    [Fact] public async Task ReadFee_UsesInstrumentQueryAndNestedSignedRates()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("fees");await using var c=f.Client();var r=await c.ReadFeesAsync("BTC-USDC",f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);var row=Assert.Single(r.Rows);var group=Assert.Single(row.Groups).Value;
        Assert.Equal("1",group.GroupId);Assert.Equal(-0.0008m,group.Maker.Value);Assert.Equal(-0.001m,group.Taker.Value);Assert.Equal("-0.0008",group.RpiMaker.Raw);
        Assert.Equal("1763979985847",row.Timestamp.Raw);Assert.Equal("?instId=BTC-USDC&instType=SPOT",r.ExactQuery);f.Done();
    }
    [Fact] public async Task ReadTime_IsPublicAndNotAccountAuthority()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("time");await using var c=f.Client();var r=await c.ReadTimeAsync(f.Context,default);
        Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Equal("1597026383085",Assert.Single(r.Rows).Ts.Raw);Assert.Equal(0,f.Opens);Assert.Single(f.Sent);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(OkxExecutionObservationHttpFixture.Control("time")))).ToLowerInvariant(),r.ResponseDigest);
        Assert.DoesNotContain(typeof(OkxObservationRead<>).GetProperties(),p=>p.Name is "IsTrusted" or "Ready" or "Complete");f.Done();
    }

    private static JsonNode Wire(string name)=>JsonNode.Parse(OkxExecutionObservationHttpFixture.Control(name))!;
    private static async Task<(OkxObservationStatus Status,string? Error,int Count)> Read(OkxExecutionObservationClient c,OkxExecutionObservationHttpFixture f,string name)
    {
        switch(name)
        {
            case "account":var a=await c.ReadAccountAsync(f.Context,default);return(a.Status,a.ErrorCode,a.Rows.Count);
            case "balance":var b=await c.ReadBalancesAsync(f.Context,default);return(b.Status,b.ErrorCode,b.Rows.Count);
            case "instruments":var i=await c.ReadInstrumentsAsync(f.Context,default);return(i.Status,i.ErrorCode,i.Rows.Count);
            case "fees":var g=await c.ReadFeesAsync("BTC-USDC",f.Context,default);return(g.Status,g.ErrorCode,g.Rows.Count);
            default:var t=await c.ReadTimeAsync(f.Context,default);return(t.Status,t.ErrorCode,t.Rows.Count);
        }
    }
    [Theory][InlineData("absent")][InlineData("empty")][InlineData("populated")]
    public async Task Read_SettleCurrencyListParentRulingPositive(string variant)
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire("account");var row=w["data"]![0]!;
        if(variant=="absent")row.AsObject().Remove("settleCcyList");else if(variant=="empty")row["settleCcyList"]=new JsonArray();
        f.Add("account",w.ToJsonString());await using var c=f.Client();Assert.Equal(OkxObservationStatus.Observed,(await c.ReadAccountAsync(f.Context,default)).Status);f.Done();
    }
    [Theory][InlineData("\"USD\"")][InlineData("null")][InlineData("1")][InlineData("{}")][InlineData("[\"USD\",false]")]
    public async Task Read_SettleCurrencyListWrongTypesRefuse(string value)
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire("account");w["data"]![0]!["settleCcyList"]=JsonNode.Parse(value);
        f.Add("account",w.ToJsonString());await using var c=f.Client();var r=await c.ReadAccountAsync(f.Context,default);
        Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal("InvalidPayload",r.ErrorCode);Assert.Empty(r.Rows);f.Done();
    }
    public static IEnumerable<object[]> ShapeMutations()
    {
        foreach(var name in new[]{"account","balance","instruments","fees","time"})
        {
            var w=Wire(name);yield return new object[]{name,w.ToJsonString().Replace("\"code\":\"0\"","\"code\":\"0\",\"Code\":\"0\"")};
            var row=w["data"]![0]!;row["unexpected"]=true;yield return new object[]{name,w.ToJsonString()};
        }
        foreach(var value in new[]{"\"false\"","0","null","[]"})
        {var w=Wire("account");w["data"]![0]!["autoLoan"]=JsonNode.Parse(value);yield return new object[]{"account",w.ToJsonString()};}
        foreach(var name in new[]{"account","balance","instruments","fees"})
        {
            var w=Wire(name);w["msg"]="LATE_PRIVATE_SENTINEL";yield return new object[]{name,w.ToJsonString().Replace("LATE_PRIVATE_SENTINEL","\\uD800")};
        }
        {var w=Wire("balance");w["data"]![0]!["details"]![0]!["Ccy"]="BTC";yield return new object[]{"balance",w.ToJsonString()};}
        {var w=Wire("fees");w["data"]![0]!["feeGroup"]![0]!["Maker"]="0";yield return new object[]{"fees",w.ToJsonString()};}
        {var w=Wire("instruments");w["data"]![0]!["upcChg"]![0]!["newValue"]=1;yield return new object[]{"instruments",w.ToJsonString()};}
        {var w=Wire("fees");w["data"]![0]!["fiat"]![0]!["ccy"]=false;yield return new object[]{"fees",w.ToJsonString()};}
        {var w=Wire("fees");var gs=w["data"]![0]!["feeGroup"]!.AsArray();gs.Add(gs[0]!.DeepClone());yield return new object[]{"fees",w.ToJsonString()};}
        foreach(var raw in new[]{"1e-2","0.00000000000000000000000000001","1.00000000000000000000000000001","79228162514264337593543950336"})
        {var w=Wire("balance");w["data"]![0]!["details"]![0]!["cashBal"]=raw;yield return new object[]{"balance",w.ToJsonString()};}
    }
    [Theory][MemberData(nameof(ShapeMutations))]
    public async Task Read_DuplicateCaseAndLateSurrogateRefuse(string name,string body)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add(name,body);await using var c=f.Client();var r=await Read(c,f,name);
        Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal("InvalidPayload",r.Error);Assert.Equal(0,r.Count);Assert.Single(f.Sent);f.Done();
    }
    [Theory][InlineData("account")][InlineData("balance")][InlineData("fees")][InlineData("time")][InlineData("instruments")]
    public async Task Read_SingletonIsNotPageCapacity(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        foreach(var count in new[]{0,2})
        {
            var w=Wire(name);var row=w["data"]![0]!.DeepClone();w["data"]=new JsonArray();for(int n=0;n<count;n++)w["data"]!.AsArray().Add(row.DeepClone());
            f.Add(name,w.ToJsonString());var r=await Read(c,f,name);
            Assert.Equal(name=="instruments"?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);
            Assert.Equal(name=="instruments"?count:0,r.Count);if(name!="instruments")Assert.Equal("Cardinality",r.Error);
        }
        f.Done();
    }
    [Fact] public async Task Read_OptionalMissingWrappersRemainUnknownAndMetadataIsNotRequired()
    {
        using var f=new OkxExecutionObservationHttpFixture();var b=Wire("balance");var d=b["data"]![0]!["details"]![0]!.AsObject();d.Remove("liab");d.Remove("uTime");
        f.Add("balance",b.ToJsonString());await using var c=f.Client();var r=await c.ReadBalancesAsync(f.Context,default);Assert.Equal(OkxObservationStatus.Observed,r.Status);
        Assert.Null(r.Rows[0].Details[0].Liability.Raw);Assert.Null(r.Rows[0].Details[0].Liability.Value);Assert.Null(r.Rows[0].Details[0].UTime.Utc);
        var w=Wire("fees");w["data"]![0]!.AsObject().Remove("fiat");w["data"]![0]!.AsObject().Remove("maker");f.Add("fees",w.ToJsonString());
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadFeesAsync("BTC-USDC",f.Context,default)).Status);f.Done();
    }
    [Theory][InlineData(429)][InlineData(503)][InlineData(302)][InlineData(200)]
    public async Task Read_NoImplicitRetryOrMutation(int status)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("account",status==200?"{\"code\":\"50011\",\"msg\":\"PRIVATE_RESPONSE_SENTINEL\",\"data\":[]}":"PRIVATE_RESPONSE_SENTINEL",status);
        await using var c=f.Client();var r=await c.ReadAccountAsync(f.Context,default);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);Assert.Single(f.Sent);
        Assert.DoesNotContain("PRIVATE_RESPONSE_SENTINEL",JsonSerializer.Serialize(r)+r);f.Done();
    }
    [Fact] public async Task Read_ProviderAndDiagnosticsStayPrivate()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.ProviderError=new Exception("PRIVATE_PROVIDER_SENTINEL",new Exception("PRIVATE_INNER"));await using var c=f.Client();
        var r=await c.ReadAccountAsync(f.Context,default);Assert.Equal("CredentialUnavailable",r.ErrorCode);Assert.Empty(f.Sent);
        Assert.DoesNotContain("PRIVATE_",JsonSerializer.Serialize(r)+r);f.ProviderError=null;var id=OkxExecutionObservationHttpFixture.NextUid();f.MaterialScope=new("Demo",id,id,"USD","other");
        Assert.Equal("CredentialBindingMismatch",(await c.ReadAccountAsync(f.Context,default)).ErrorCode);Assert.Empty(f.Sent);
        f.MaterialScope=null;using var listener=new System.Diagnostics.DiagnosticListener("HttpHandlerDiagnosticListener");using(var sub=listener.Subscribe(new Observer()))
        { var before=f.Opens;Assert.Equal("UnsafeHttpDiagnostics",(await c.ReadAccountAsync(f.Context,default)).ErrorCode);Assert.Equal(before,f.Opens);Assert.Empty(f.Sent); }
        f.Add("time");Assert.Equal(OkxObservationStatus.Observed,(await c.ReadTimeAsync(f.Context,default)).Status);Assert.Equal(2,f.Opens);f.Done();
    }
    private sealed class Observer : IObserver<KeyValuePair<string,object?>> { public void OnNext(KeyValuePair<string,object?> v){}public void OnCompleted(){}public void OnError(Exception e){} }
    [Theory][InlineData("utc-equality")][InlineData("mono-equality")][InlineData("mono-expiry")][InlineData("rollback")][InlineData("stop-provider")][InlineData("cancel-provider")][InlineData("dispose-provider")]
    public async Task Read_BudgetFenceAndDisposeAfterProvider(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();f.Add("account");Assert.Equal(OkxObservationStatus.Observed,(await c.ReadAccountAsync(f.Context,default)).Status);
        // Start the second call at its quota boundary so the ten-second cases are true equality, not 10.6s.
        f.Clock.Advance(TimeSpan.FromMilliseconds(600));
        using var cancel=new CancellationTokenSource();Task? disposal=null;
        f.OnOpen=()=>{switch(change){case "utc-equality":f.Clock.Utc+=TimeSpan.FromSeconds(10);break;case "mono-equality":f.Clock.Mono+=TimeSpan.FromSeconds(10).Ticks;break;case "mono-expiry":f.Clock.Mono+=TimeSpan.FromSeconds(11).Ticks;break;case "rollback":f.Clock.Utc-=TimeSpan.FromSeconds(1);break;case "stop-provider":f.Allowed=false;break;case "cancel-provider":cancel.Cancel();break;default:disposal=c.DisposeAsync().AsTask();break;}};
        var r=await c.ReadAccountAsync(f.Context,cancel.Token);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Single(f.Sent);Assert.Empty(r.Rows);if(disposal is not null)await disposal;f.Done();
    }
    [Theory][InlineData("stop")][InlineData("expiry")][InlineData("mono-rollback")][InlineData("cancel")][InlineData("dispose")]
    public async Task Read_BudgetFenceAndDisposeAfterWait(string change)
    {
        using var f=new OkxExecutionObservationHttpFixture();using var cancel=new CancellationTokenSource();Task? disposal=null;OkxExecutionObservationClient? client=null;
        f.Delay=(t,ct)=>{f.Clock.Advance(t);switch(change){case "stop":f.Allowed=false;break;case "expiry":f.Clock.Advance(TimeSpan.FromSeconds(10));break;case "mono-rollback":f.Clock.Mono+=TimeSpan.FromSeconds(11).Ticks;f.Clock.Utc-=TimeSpan.FromSeconds(1);break;case "cancel":cancel.Cancel();break;default:disposal=client!.DisposeAsync().AsTask();break;}return Task.CompletedTask;};
        await using var c=f.Client();client=c;f.Add("account");Assert.Equal(OkxObservationStatus.Observed,(await c.ReadAccountAsync(f.Context,default)).Status);
        var r=await c.ReadBalancesAsync(f.Context,cancel.Token);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Single(f.Sent);Assert.Equal(1,f.Opens);if(disposal is not null)await disposal;f.Done();
    }
    [Fact] public async Task Read_FinalFenceClockMutationCannotSend()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();f.Add("account");Assert.Equal(OkxObservationStatus.Observed,(await c.ReadAccountAsync(f.Context,default)).Status);
        f.OnFence=()=>f.Clock.Advance(TimeSpan.FromSeconds(10));Assert.Equal(OkxObservationStatus.Failed,(await c.ReadBalancesAsync(f.Context,default)).Status);Assert.Single(f.Sent);f.Done();
    }
    [Fact] public async Task Read_CancellationDrainsOwnedInvocationAndQueuedSends()
    {
        using var f=new OkxExecutionObservationHttpFixture();var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Steps.Enqueue((OkxExecutionObservationHttpFixture.PathFor("account"),async ct=>{started.SetResult();await Task.Delay(Timeout.Infinite,ct);throw new InvalidOperationException();}));
        var c=f.Client();var first=c.ReadAccountAsync(f.Context,default);await started.Task.WaitAsync(TimeSpan.FromSeconds(3));var queued=c.ReadBalancesAsync(f.Context,default);
        await c.DisposeAsync();Assert.Equal(OkxObservationStatus.Failed,(await first).Status);Assert.Equal(OkxObservationStatus.Failed,(await queued).Status);Assert.Single(f.Sent);
        Assert.Equal(OkxObservationStatus.Failed,(await c.ReadInstrumentsAsync(f.Context,default)).Status);f.Done();
    }
    [Fact] public async Task Read_QuotaSharedByExpectedUidAcrossBindings()
    {
        using var a=new OkxExecutionObservationHttpFixture();using var b=new OkxExecutionObservationHttpFixture();b.Scope=new("Demo",a.Scope.Uid,a.Scope.Uid,"USD","second");
        a.Add("account");var c=a.Client();Assert.Equal("654321",Assert.Single((await c.ReadAccountAsync(a.Context,default)).Rows).Uid);await c.DisposeAsync();
        b.Add("balance");b.Add("instruments");b.Add("fees");await using(var d=b.Client())
        {Assert.Equal(OkxObservationStatus.Observed,(await d.ReadBalancesAsync(b.Context,default)).Status);Assert.Equal(OkxObservationStatus.Observed,(await d.ReadInstrumentsAsync(b.Context,default)).Status);Assert.Equal(OkxObservationStatus.Observed,(await d.ReadFeesAsync("BTC-USDC",b.Context,default)).Status);}
        var starts=a.Sent.Concat(b.Sent).Select(s=>s.Utc).ToArray();for(int n=1;n<starts.Length;n++)Assert.True(starts[n]-starts[n-1]>=TimeSpan.FromMilliseconds(600));
        await using var recreated=b.Client();b.Add("account");Assert.Equal(OkxObservationStatus.Observed,(await recreated.ReadAccountAsync(b.Context,default)).Status);Assert.True(b.Sent[^1].Utc-b.Sent[^2].Utc>=TimeSpan.FromMilliseconds(600));a.Done();b.Done();
    }
    [Fact] public async Task Read_PublicQuotaIsGlobalAndIndependentOfPrivateBindings()
    {
        using var a=new OkxExecutionObservationHttpFixture();using var b=new OkxExecutionObservationHttpFixture();a.Add("time");b.Add("time");await using var c=a.Client();await using var d=b.Client();
        Assert.Equal(OkxObservationStatus.Observed,(await c.ReadTimeAsync(a.Context,default)).Status);Assert.Equal(OkxObservationStatus.Observed,(await d.ReadTimeAsync(b.Context,default)).Status);
        Assert.True(b.Sent[0].Utc-a.Sent[0].Utc>=TimeSpan.FromMilliseconds(250));Assert.Equal(0,a.Opens+b.Opens);a.Done();b.Done();
    }
    [Theory][InlineData("account")][InlineData("balance")][InlineData("fees")][InlineData("instruments")]
    public async Task Read_ResourceCapsAreFailureNotTruncation(string name)
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire(name);
        if(name=="account")w["msg"]=new string('x',1048576);
        else
        {
            var array=name=="balance"?w["data"]![0]!["details"]!.AsArray():name=="fees"?w["data"]![0]!["feeGroup"]!.AsArray():w["data"]!.AsArray();
            var row=array[0]!.DeepClone();array.Clear();for(int n=0;n<(name=="instruments"?4097:257);n++)array.Add(row.DeepClone());
        }
        f.Add(name,w.ToJsonString());await using var c=f.Client();var r=await Read(c,f,name);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal(0,r.Count);Assert.Contains(r.Error,new[]{"BodyLimit","Cardinality"});f.Done();
    }
    public static IEnumerable<object[]> DocumentedFields()
    {
        var endpoints=OkxExecutionObservationHttpFixture.Oracle()["endpoints"]!.AsObject();
        foreach(var e in endpoints)foreach(var field in e.Value!["fields"]!.AsArray())
            yield return new object[]{e.Key,field!["field"]!.GetValue<string>(),field["type"]!.GetValue<string>(),field["presence"]!.GetValue<string>()};
    }
    private static (JsonObject Parent,string Field) FieldAt(JsonNode w,string field)
    {
        var parts=field.Split("[].",StringSplitOptions.None);var row=w["data"]![0]!.AsObject();
        return parts.Length==1?(row,field):(row[parts[0]]![0]!.AsObject(),parts[1]);
    }
    [Theory][MemberData(nameof(DocumentedFields))]
    public async Task Read_EveryDocumentedOptionalFieldTypeValidatedIndependently(string name,string field,string type,string presence)
    {
        Assert.Contains(presence,new[]{"selected essential","optional; validate type if present"});
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire(name);var at=FieldAt(w,field);
        // Start from an actual populated official-shaped control before changing exactly one wire type.
        at.Parent[at.Field]=type.StartsWith("Array")?new JsonObject():type.StartsWith("Integer")?JsonValue.Create("1"):type=="Boolean"?JsonValue.Create("false"):JsonValue.Create(false);
        f.Add(name,w.ToJsonString());await using var c=f.Client();var r=await Read(c,f,name);
        Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal("InvalidPayload",r.Error);Assert.Equal(0,r.Count);f.Done();
    }
    [Theory][MemberData(nameof(DocumentedFields))]
    public async Task Read_AllDocumentedFieldTypesHavePositiveShapeControls(string name,string field,string type,string presence)
    {
        Assert.Contains(presence,new[]{"selected essential","optional; validate type if present"});
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire(name);var at=FieldAt(w,field);
        if(!at.Parent.ContainsKey(at.Field))at.Parent[at.Field]=type.StartsWith("Array")?new JsonArray():type.StartsWith("Integer")?JsonValue.Create(100):type=="Boolean"?JsonValue.Create(false):JsonValue.Create("");
        f.Add(name,w.ToJsonString());await using var c=f.Client();Assert.Equal(OkxObservationStatus.Observed,(await Read(c,f,name)).Status);f.Done();
    }
    public static IEnumerable<object[]> OptionalDocumentedFields()=>DocumentedFields().Where(x=>(string)x[3]!="selected essential");
    [Theory][MemberData(nameof(OptionalDocumentedFields))]
    public async Task Read_DocumentedOptionalFieldMayBeAbsent(string name,string field,string type,string presence)
    {
        Assert.NotEmpty(type);Assert.Equal("optional; validate type if present",presence);
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire(name);var at=FieldAt(w,field);at.Parent.Remove(at.Field);
        f.Add(name,w.ToJsonString());await using var c=f.Client();Assert.Equal(OkxObservationStatus.Observed,(await Read(c,f,name)).Status);f.Done();
    }
    [Theory][InlineData("account","uid")][InlineData("account","autoLoan")][InlineData("balance","details")][InlineData("instruments","tradeQuoteCcyList")][InlineData("fees","feeGroup")][InlineData("time","ts")]
    public async Task Read_MissingSelectedEssentialRefuses(string name,string field)
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire(name);w["data"]![0]!.AsObject().Remove(field);f.Add(name,w.ToJsonString());await using var c=f.Client();var r=await Read(c,f,name);
        Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Equal("InvalidPayload",r.Error);Assert.Equal(0,r.Count);f.Done();
    }
    [Fact] public async Task Read_EmptyFeeGroupsCannotFallBackToDeprecatedRates()
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire("fees");w["data"]![0]!["feeGroup"]=new JsonArray();f.Add("fees",w.ToJsonString());await using var c=f.Client();
        var r=await c.ReadFeesAsync("BTC-USDC",f.Context,default);Assert.Equal(OkxObservationStatus.Observed,r.Status);Assert.Empty(Assert.Single(r.Rows).Groups);f.Done();
    }
    [Theory][InlineData("51603")][InlineData("50011")]
    public async Task Read_BoundedVenueCodesAreNotSuccessOrAuthority(string code)
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("account","{\"code\":\""+code+"\",\"msg\":\"PRIVATE_SENTINEL\",\"data\":[]}");await using var c=f.Client();var r=await c.ReadAccountAsync(f.Context,default);
        Assert.Equal(code,r.ErrorCode);Assert.Equal(OkxObservationStatus.Failed,r.Status);Assert.Empty(r.Rows);Assert.Single(f.Sent);Assert.DoesNotContain("PRIVATE_SENTINEL",JsonSerializer.Serialize(r));f.Done();
    }
    [Fact] public async Task Read_InstrumentRowCapIsIndependentOfBodyLimit()
    {
        using var f=new OkxExecutionObservationHttpFixture();var w=Wire("instruments");var minimal=JsonNode.Parse("{\"instId\":\"BTC-USDC\",\"instType\":\"SPOT\",\"baseCcy\":\"BTC\",\"quoteCcy\":\"USDC\",\"tradeQuoteCcyList\":[],\"state\":\"live\"}")!;
        w["data"]=new JsonArray();for(int n=0;n<4097;n++)w["data"]!.AsArray().Add(minimal.DeepClone());var body=w.ToJsonString();Assert.True(Encoding.UTF8.GetByteCount(body)<1048576);
        f.Add("instruments",body);await using var c=f.Client();var r=await c.ReadInstrumentsAsync(f.Context,default);Assert.Equal("Cardinality",r.ErrorCode);Assert.Empty(r.Rows);f.Done();
    }
    [Fact] public async Task Read_StrictPeerAssertionsAreNotSwallowed()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("balance");await using var c=f.Client();await Assert.ThrowsAnyAsync<Xunit.Sdk.XunitException>(()=>c.ReadAccountAsync(f.Context,default));
    }
    [Fact] public async Task Read_ResponseLifetimeOwnsOneClearedNonGrowingBuffer()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        foreach(var kind in new[]{"valid","malformed","overflow","stream-failure","cancel","invalid-utf8","depth"})
        {
            byte[] wire=Encoding.UTF8.GetBytes(kind=="valid"?OkxExecutionObservationHttpFixture.Control("account"):kind=="overflow"?new string('x',1048577):kind=="depth"?"{\"code\":\"0\",\"msg\":\"\",\"data\":"+new string('[',33)+"0"+new string(']',33)+"}":"PRIVATE_STREAM_SENTINEL");
            if(kind=="invalid-utf8")wire=[123,34,109,115,103,34,58,34,255,34,125];
            var stream=new CapturingStream(wire,kind=="stream-failure",kind=="cancel");
            f.Steps.Enqueue((OkxExecutionObservationHttpFixture.PathFor("account"),_=>Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK){Content=new System.Net.Http.StreamContent(stream)})));
            var r=await c.ReadAccountAsync(f.Context,default);Assert.Equal(kind=="valid"?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);
            Assert.NotNull(stream.Owned);Assert.Equal(1048577,stream.Owned!.Length);Assert.All(stream.Owned,b=>Assert.Equal((byte)0,b));
            if(kind=="overflow")Assert.Equal("BodyLimit",r.ErrorCode);if(kind=="depth"||kind=="invalid-utf8")Assert.Equal("InvalidPayload",r.ErrorCode);
            Assert.DoesNotContain("PRIVATE_STREAM_SENTINEL",JsonSerializer.Serialize(r));
        }
        f.Done();
    }
    // Observe the actual array handed to Stream.ReadAsync; no production-only test hooks or allocators.
    private sealed class CapturingStream(byte[] data,bool fail,bool cancel) : Stream
    {
        internal byte[]? Owned;private int offset;
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>throw new NotSupportedException();set=>throw new NotSupportedException();}
        public override ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken ct=default)
        {
            Assert.True(System.Runtime.InteropServices.MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)buffer,out var segment));
            if(Owned is not null)Assert.Same(Owned,segment.Array);Owned=segment.Array;
            if(offset>0&&fail)throw new IOException("PRIVATE_STREAM_SENTINEL");if(offset>0&&cancel)throw new OperationCanceledException("PRIVATE_STREAM_SENTINEL");
            int n=Math.Min(Math.Min(buffer.Length,4096),data.Length-offset);data.AsMemory(offset,n).CopyTo(buffer);offset+=n;return ValueTask.FromResult(n);
        }
        public override int Read(byte[] b,int o,int n)=>throw new NotSupportedException();public override void Flush()=>throw new NotSupportedException();
        public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int n)=>throw new NotSupportedException();
    }
    [Fact] public async Task Read_OwnedSigningMaterialClearsOnEveryExit()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        foreach(var kind in new[]{"valid","malformed","http-error","binding","stop","transport"})
        {
            f.MaterialScope=null;f.OnOpen=null;
            if(kind=="binding") {var id=OkxExecutionObservationHttpFixture.NextUid();f.MaterialScope=new("Demo",id,id,"USD","mismatch");}
            else if(kind=="stop")f.OnOpen=()=>f.Allowed=false;
            else if(kind=="transport")f.Steps.Enqueue((OkxExecutionObservationHttpFixture.PathFor("account"),_=>throw new HttpRequestException("PRIVATE_TRANSPORT_SENTINEL")));
            else f.Add("account",kind=="malformed"?"PRIVATE_BAD_JSON":null,kind=="http-error"?503:200);
            var r=await c.ReadAccountAsync(f.Context,default);Assert.Equal(kind=="valid"?OkxObservationStatus.Observed:OkxObservationStatus.Failed,r.Status);
            Assert.NotNull(f.LastMaterial);Assert.All(f.LastMaterial!.Key.Concat(f.LastMaterial.Secret).Concat(f.LastMaterial.Passphrase),b=>Assert.Equal((byte)0,b));
            f.Allowed=true;Assert.DoesNotContain("PRIVATE_",JsonSerializer.Serialize(r));
        }
        f.Done();
    }
    [Fact] public async Task Read_FailedAttemptsConsumeQuotaAndEnvironmentLanesStaySeparate()
    {
        using var f=new OkxExecutionObservationHttpFixture();f.Add("account","{\"code\":\"50011\",\"msg\":\"\",\"data\":[]}");f.Add("balance");await using var c=f.Client();
        Assert.Equal("50011",(await c.ReadAccountAsync(f.Context,default)).ErrorCode);Assert.Equal(OkxObservationStatus.Observed,(await c.ReadBalancesAsync(f.Context,default)).Status);
        Assert.True(f.Sent[1].Utc-f.Sent[0].Utc>=TimeSpan.FromMilliseconds(600));
        using var g=new OkxExecutionObservationHttpFixture();g.Scope=new("Live",f.Scope.Uid,f.Scope.Uid,"USD","separate-env");g.Add("account");await using var d=g.Client();
        Assert.Equal(OkxObservationStatus.Observed,(await d.ReadAccountAsync(g.Context,default)).Status);Assert.Equal(f.Sent[0].Utc,g.Sent[0].Utc);f.Done();g.Done();
    }
    [Fact] public async Task Read_InvalidClosedInputsNeverInvoke()
    {
        using var f=new OkxExecutionObservationHttpFixture();await using var c=f.Client();
        Assert.Equal(OkxObservationStatus.Failed,(await c.ReadAccountAsync(new(Guid.Empty,Guid.NewGuid()),default)).Status);
        foreach(var inst in new[]{"BTC-USDC&instType=MARGIN","BTC%2DUSDC","BTC-USDC-SWAP","btc-usdc"})Assert.Equal(OkxObservationStatus.Failed,(await c.ReadFeesAsync(inst,f.Context,default)).Status);
        Assert.Empty(f.Sent);Assert.Equal(0,f.Opens);
    }
}
