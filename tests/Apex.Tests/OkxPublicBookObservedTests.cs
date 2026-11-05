using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.Tests;
public sealed class OkxPublicBookObservedTests
{
    public static IEnumerable<object[]> PublicBytes()
    {
        yield return new object[]{"BTC-EUR","{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"69600\",\"0.00020479\",\"0\",\"1\"]],\"bids\": [[\"69524.3\",\"0.00020479\",\"0\",\"1\"]],\"ts\": \"1793777345208\",\"seqId\": 452038725}]}"};
        yield return new object[]{"ETH-EUR","{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"2048.1\",\"0.0061024\",\"0\",\"1\"]],\"bids\": [[\"2045.4\",\"0.0881577\",\"0\",\"1\"]],\"ts\": \"1793777345408\",\"seqId\": 66265469}]}"};
        yield return new object[]{"BTC-USDC","{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"78244\",\"0.0001\",\"0\",\"1\"]],\"bids\": [[\"78232\",\"0.0001\",\"0\",\"1\"]],\"ts\": \"1793777305604\",\"seqId\": 48304975}]}"};
        yield return new object[]{"SOL-USDC","{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"93.2\",\"4.59195\",\"0\",\"4\"]],\"bids\": [[\"93\",\"0.01\",\"0\",\"1\"]],\"ts\": \"1793777345554\",\"seqId\": 2165309}]}"};
        yield return new object[]{"BTC-USDT","{\"code\":\"0\",\"msg\":\"\",\"data\":[{\"asks\":[[\"78306.3\",\"0.00007281\",\"0\",\"1\"]],\"bids\": [[\"78306.2\",\"0.00007281\",\"0\",\"1\"]],\"ts\": \"1793777305758\",\"seqId\": 120123386}]}"};
    }
    private sealed class Clock(DateTimeOffset now):TimeProvider
    {public override DateTimeOffset GetUtcNow()=>now;public override long GetTimestamp()=>123;}
    private sealed class Peer(string body):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(body)});}
    [Theory,MemberData(nameof(PublicBytes))]
    public async Task Exact_new_unsigned_bytes_keep_original_price_depth_source_and_decimal_bits(string instrument,string body)
    {
        using var doc=JsonDocument.Parse(body);var row=doc.RootElement.GetProperty("data")[0];var asks=row.GetProperty("asks")[0];var bids=row.GetProperty("bids")[0];
        var source=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row.GetProperty("ts").GetString()!,CultureInfo.InvariantCulture));var clock=new Clock(source.AddMilliseconds(100));
        var parts=instrument.Split('-');var spec=new NavalInstrument(instrument,parts[0],parts[1],.0001m,.0001m,.01m);
        using var feed=new OkxPublicMarketFeed(clock,new Peer(body));var q=await feed.ReadQuoteAsync(spec,default);
        Assert.Equal(instrument,q.Instrument);Assert.Equal(source,q.SourceUtc);Assert.Equal(clock.GetUtcNow(),q.ReceivedUtc);Assert.Equal(123,q.ReceivedTimestamp);
        foreach(var pair in new[]{(q.Bid,bids[0].GetString()!),(q.Ask,asks[0].GetString()!),(q.AskQuantity,asks[1].GetString()!)})
            Assert.Equal(decimal.GetBits(decimal.Parse(pair.Item2,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture)),decimal.GetBits(pair.Item1));
    }
    [Theory]
    [InlineData("0.0000000000000000000000000001")]
    [InlineData("1.0000000000000000000000000000")]
    [InlineData("1234567890123456789012345678")]
    [InlineData("0")]
    public async Task Existing_decimal_grammar_and_local_scale_are_not_relaxed_or_rounded(string value)
    {
        var original=(string)PublicBytes().First()[1];var root=JsonNode.Parse(original)!;root["data"]![0]!["asks"]![0]![0]=value;root["data"]![0]!["asks"]![0]![1]=value;root["data"]![0]!["bids"]![0]![0]=value;
        var spec=new NavalInstrument("BTC-EUR","BTC","EUR",.0001m,.0001m,.01m);using var feed=new OkxPublicMarketFeed(new Clock(DateTimeOffset.UtcNow),new Peer(root.ToJsonString()));var q=await feed.ReadQuoteAsync(spec,default);
        foreach(var actual in new[]{q.Ask,q.Bid,q.AskQuantity})Assert.Equal(decimal.GetBits(decimal.Parse(value,CultureInfo.InvariantCulture)),decimal.GetBits(actual));
    }
    [Fact]
    public async Task Source_future_and_zero_are_left_to_unchanged_caller_guards_not_hidden_fallbacks()
    {
        var original=(string)PublicBytes().First()[1];var clock=new Clock(DateTimeOffset.FromUnixTimeMilliseconds(1));using var feed=new OkxPublicMarketFeed(clock,new Peer(original));
        var q=await feed.ReadQuoteAsync(new("BTC-EUR","BTC","EUR",.0001m,.0001m,.01m),default);Assert.True(q.SourceUtc>q.ReceivedUtc);
    }
}
