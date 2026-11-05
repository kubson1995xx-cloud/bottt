using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Adapters.Okx.Execution;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;

// Original causal tester uses only accepted APIs. Its unchanged executable and
// literal book/funding/fee operands are replayed against old/final/extracted DLLs.
public sealed class OkxFiniteEmptyBookTests
{
    internal const string ActualShapedEmpty="{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\"1793777345208\",\"seqId\":3235851742}]}";
    private const string Valid="{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"110\",\"100\",\"0\",\"1\"]],\"bids\":[[\"109.99\",\"100\",\"0\",\"1\"]],\"ts\":\"1793777345208\",\"seqId\":3235851742}]}";
    private sealed class Clock:TimeProvider
    {
        internal static readonly DateTimeOffset Now=DateTimeOffset.FromUnixTimeMilliseconds(1793777345308);
        private long mono=10000000,wall;
        public override DateTimeOffset GetUtcNow()=>Now+TimeSpan.FromTicks(wall);
        public override long GetTimestamp()=>mono;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        internal Task Delay(TimeSpan t,CancellationToken ct){ct.ThrowIfCancellationRequested();mono+=t.Ticks;wall+=t.Ticks;return Task.CompletedTask;}
    }
    private sealed class BookPeer(string scenario):HttpMessageHandler
    {
        internal readonly List<string> Books=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method);Assert.Null(r.Content);Assert.Empty(r.Headers.UserAgent);
            Assert.Equal("https",r.RequestUri!.Scheme);Assert.Equal("eea.okx.com",r.RequestUri.Host);Assert.Equal("/api/v5/market/books",r.RequestUri.AbsolutePath);
            Assert.Equal("1",r.Headers.GetValues("x-simulated-trading").Single());Assert.DoesNotContain(r.Headers,h=>h.Key.StartsWith("OK-ACCESS-",StringComparison.OrdinalIgnoreCase));
            string instrument=r.RequestUri.Query switch{"?instId=BCH-EUR&sz=1"=>"BCH-EUR","?instId=LTC-EUR&sz=1"=>"LTC-EUR","?instId=ADA-USDC&sz=1"=>"ADA-USDC",_=>throw new Exception("UNEXPECTED_PUBLIC_QUERY")};Books.Add(instrument);
            string action=instrument=="BCH-EUR"?scenario:scenario is "all-empty" or "all-infeasible"?"both-empty":"valid";
            if(action=="transport")throw new HttpRequestException("PRIVATE_FAILURE_SENTINEL");
            if(action=="cancel")throw new OperationCanceledException("PRIVATE_FAILURE_SENTINEL");
            var root=JsonNode.Parse(action=="valid"||action=="equal-price"?Valid:ActualShapedEmpty)!;var row=root["data"]![0]!;
            switch(action)
            {
                case "bid-empty":row["asks"]=JsonNode.Parse(Valid)!["data"]![0]!["asks"]!.DeepClone();row["bids"]=new JsonArray();break;
                case "both-empty":case "all-empty":case "all-infeasible":row["bids"]=new JsonArray();break;
                case "opposite-missing":row.AsObject().Remove("bids");break;
                case "opposite-kind":row["bids"]=null;break;
                case "opposite-depth":row["bids"]!.AsArray().Add(row["bids"]![0]!.DeepClone());break;
                case "opposite-fields":row["bids"]![0]!.AsArray().RemoveAt(3);break;
                case "opposite-price":row["bids"]![0]![0]="1e2";break;
                case "opposite-zero-price":row["bids"]![0]![0]="0";break;
                case "opposite-size":row["bids"]![0]![1]="-1";break;
                case "opposite-zero-size":row["bids"]![0]![1]="0";break;
                case "deprecated":row["bids"]![0]![2]="1";break;
                case "orders":row["bids"]![0]![3]="1.5";break;
                case "orders-zero":row["bids"]![0]![3]="0";break;
                case "metadata":row["seqId"]="unknown";break;
                case "unknown":row["PRIVATE_FIELD_SENTINEL"]="unknown";break;
                case "envelope":root["PRIVATE_FIELD_SENTINEL"]="unknown";break;
                case "timestamp":row["ts"]="unknown";break;
                case "timestamp-kind":row["ts"]=123;break;
                case "timestamp-space":row["ts"]=" 1793777345208";break;
                case "future":row["ts"]="1793777355308";break;
                case "stale":row["ts"]="1793777340308";break;
                case "provider":root["code"]="51001";break;
                case "data-two":root["data"]!.AsArray().Add(row.DeepClone());break;
                case "nonempty-depth":row["asks"]=JsonNode.Parse(Valid)!["data"]![0]!["asks"]!.DeepClone();row["asks"]!.AsArray().Add(row["asks"]![0]!.DeepClone());break;
                case "nonempty-number":row["asks"]=JsonNode.Parse(Valid)!["data"]![0]!["asks"]!.DeepClone();row["asks"]![0]![0]="1e2";break;
                case "nonempty-stale":row["asks"]=JsonNode.Parse(Valid)!["data"]![0]!["asks"]!.DeepClone();row["ts"]="1793777340308";break;
                case "nonempty-future":row["asks"]=JsonNode.Parse(Valid)!["data"]![0]!["asks"]!.DeepClone();row["ts"]="1793777355308";break;
            }
            // Lower USDC cost never outranks independently feasible EUR; within
            // EUR, LTC's literal lower debit beats the earlier ordinal BCH.
            if(action=="valid"&&instrument!="BCH-EUR")
            {row["asks"]![0]![0]=instrument=="LTC-EUR"&&scenario!="equal-price"?"100":instrument=="ADA-USDC"?"1":"110";row["bids"]![0]![0]=instrument=="LTC-EUR"&&scenario!="equal-price"?"99.99":instrument=="ADA-USDC"?"0.99":"109.99";}
            string body=action=="malformed"?"PRIVATE_BODY_SENTINEL":action=="duplicate"?ActualShapedEmpty.Replace("\"code\":\"0\"","\"code\":\"0\",\"Code\":\"0\""):root.ToJsonString();
            return Task.FromResult(new HttpResponseMessage(action=="http"?HttpStatusCode.ServiceUnavailable:action=="http-201"?HttpStatusCode.Created:HttpStatusCode.OK){Content=new StringContent(body)});
        }
    }
    private sealed class Fixture:IAsyncDisposable
    {
        internal readonly OperationalPeer Owner=new(finite:true);
        internal readonly Clock Time=new();
        internal readonly BookPeer Books;
        private readonly FiniteDemoPeer peer;
        internal OkxFiniteSelectionDiagnostic? Boundary;
        internal Fixture(string scenario)
        {
            Books=new(scenario);peer=new(Owner){BaseCurrency="ADA"};
            Owner.Mutate=(path,body)=>
            {
                OkxCurrentMarketFundingTests.Shape(Owner,peer,path,body);
                if(path=="/api/v5/account/balance")
                {
                    var rows=body["data"]![0]!["details"]!.AsArray();var eur=rows[0]!.DeepClone();eur["ccy"]="EUR";rows.Add(eur);
                    body["data"]![0]!["uTime"]="1793777345208";foreach(var v in rows)v!["uTime"]="1793777345208";
                }
                if(path=="/api/v5/account/instruments")
                {
                    var template=body["data"]![0]!.DeepClone();var rows=body["data"]!.AsArray();rows.Clear();
                    foreach(var pair in new[]{("BCH","EUR"),("LTC","EUR"),("ADA","USDC")})
                    {var v=template.DeepClone();v["instId"]=pair.Item1+"-"+pair.Item2;v["baseCcy"]=pair.Item1;v["quoteCcy"]=pair.Item2;v["tradeQuoteCcyList"]=new JsonArray(pair.Item2);if(scenario=="all-infeasible"&&pair.Item1=="BCH")v["minSz"]="1";rows.Add(v);}
                }
                if(path=="/api/v5/account/trade-fee")body["data"]![0]!["ts"]="1793777345208";
            };
        }
        internal Task<OkxFiniteNomination> Select()
        {
            var credentials=new Apex.Okx.DemoAcceptance.EphemeralCredentials(OperationalPeer.Key.ToArray(),OperationalPeer.Secret.ToArray(),OperationalPeer.Pass.ToArray());
            return SelectOwned(credentials);
        }
        private async Task<OkxFiniteNomination> SelectOwned(Apex.Okx.DemoAcceptance.EphemeralCredentials credentials)
        {
            using(credentials)
            {
                var scope=new OkxExecutionScope("Demo",Owner.Binding.Account.AccountId,Owner.Binding.MainUid,"USDC",Owner.Binding.CredentialBindingId);
                var premise=new NavalFiniteOperatorPremise("finite-operator-premise-v1","current-market-funding-v1","current-market-funding-bounded-demo-v1",new string('a',64),"Nie będę wykonywać innych operacji podczas\nWirtualne środki i nie korzystałem z pożyczek","parent-confirmed-user",Clock.Now.AddSeconds(-1),Clock.Now.AddMinutes(10),"OkxDemo");
                return await OkxFiniteAccountSelection.ObserveFundedAsync(scope,credentials.ForObservation(scope),credentials.Fingerprint,()=>true,default,premise,null,Time,(s,k,h,x)=>OkxExecutionObservationClient.ForTests(Owner,Time,s,k,h,x,Time.Delay),Books,b=>Boundary=b);
            }
        }
        internal void NoOwner()
        {Assert.Empty(peer.Sends);Assert.False(File.Exists(Owner.Target));Assert.Empty(Directory.EnumerateFiles(Owner.Root,".naval-broker-initialized-v2",SearchOption.AllDirectories));Assert.All(Owner.Methods,m=>Assert.Equal(HttpMethod.Get,m));}
        public async ValueTask DisposeAsync(){Books.Dispose();peer.Dispose();await Owner.DisposeAsync();}
    }
    [Theory]
    [InlineData("ask-empty")][InlineData("bid-empty")][InlineData("both-empty")]
    public async Task Confirmed_fresh_empty_side_skips_only_unexecutable_BCH_and_nominates_existing_feasible_EUR(string side)
    {
        await using var f=new Fixture(side);OkxFiniteNomination? n=null;var error=await Record.ExceptionAsync(async()=>n=await f.Select());
        Assert.True(error==null,"EXPECTED_FEASIBLE_EUR_NOMINATION_NOT_FIRST_BOOK_ABORT:"+JsonSerializer.Serialize(f.Boundary));
        Assert.Equal("LTC-EUR",n!.Instrument.Instrument);Assert.Equal("EUR",n.Quote);Assert.Equal(1234.56m,n.Cash);Assert.Equal(.02m,n.Plan.Debit);
        Assert.Equal(new[]{"BCH-EUR","LTC-EUR"},f.Books.Books);Assert.Equal(6,f.Owner.Paths.Count);
        Assert.Equal("NOMINATED",f.Boundary!.Stage);using var doc=JsonDocument.Parse(JsonSerializer.Serialize(n));
        Assert.True(doc.RootElement.TryGetProperty("UnavailableBooks",out var a),"AVAILABILITY_ATTEMPT_NOT_RETAINED");Assert.Equal(1,a.GetArrayLength());Assert.Equal("BCH-EUR",a[0].GetProperty("Instrument").GetString());Assert.Equal("FINITE_BOOK_SIDE_UNAVAILABLE",a[0].GetProperty("Reason").GetString());f.NoOwner();
    }
    [Theory]
    [InlineData("all-empty")][InlineData("all-infeasible")]
    public async Task All_confirmed_empty_or_infeasible_candidates_finish_no_feasible_with_attempts_and_no_owner(string scenario)
    {
        await using var f=new Fixture(scenario);var e=await Assert.ThrowsAsync<OkxFiniteAccountRefusal>(()=>f.Select());Assert.Equal("FINITE_NO_FEASIBLE_ROUNDTRIP_FOR_FUNDED_QUOTES",e.Code);
        Assert.Contains("FINITE_BOOK_SIDE_UNAVAILABLE",JsonSerializer.Serialize(e.Operands));Assert.Equal(new[]{"BCH-EUR","LTC-EUR","ADA-USDC"},f.Books.Books);f.NoOwner();
    }
    [Theory]
    [InlineData("opposite-missing")][InlineData("opposite-kind")][InlineData("opposite-depth")][InlineData("opposite-fields")]
    [InlineData("opposite-price")][InlineData("opposite-zero-price")][InlineData("opposite-size")][InlineData("opposite-zero-size")]
    [InlineData("deprecated")][InlineData("orders")][InlineData("orders-zero")][InlineData("metadata")][InlineData("unknown")][InlineData("envelope")]
    [InlineData("timestamp")][InlineData("timestamp-kind")][InlineData("timestamp-space")][InlineData("future")][InlineData("stale")]
    [InlineData("provider")][InlineData("transport")][InlineData("cancel")][InlineData("http")][InlineData("http-201")][InlineData("malformed")][InlineData("duplicate")][InlineData("data-two")]
    [InlineData("nonempty-depth")][InlineData("nonempty-number")][InlineData("nonempty-stale")][InlineData("nonempty-future")]
    public async Task First_uncertainty_is_never_relabelled_unavailable_or_queries_next_candidate(string scenario)
    {
        await using var f=new Fixture(scenario);var e=await Record.ExceptionAsync(()=>f.Select());Assert.NotNull(e);
        Assert.False(e is OkxFiniteAccountRefusal p&&p.Code=="FINITE_NO_FEASIBLE_ROUNDTRIP_FOR_FUNDED_QUOTES","UNCERTAINTY_SWALLOWED_AS_INFEASIBLE");
        Assert.Equal(new[]{"BCH-EUR"},f.Books.Books);Assert.Equal(5,f.Owner.Paths.Count);f.NoOwner();
    }
    [Theory]
    [InlineData("valid","LTC-EUR")][InlineData("equal-price","BCH-EUR")]
    public async Task Existing_quote_priority_minimum_debit_and_ordinal_tie_are_unchanged(string scenario,string expected)
    {await using var f=new Fixture(scenario);var n=await f.Select();Assert.Equal(expected,n.Instrument.Instrument);Assert.Equal("EUR",n.Quote);Assert.Equal(new[]{"BCH-EUR","LTC-EUR"},f.Books.Books);f.NoOwner();}
}
