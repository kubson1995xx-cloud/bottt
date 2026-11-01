using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Closed public EEA cash-SPOT feed. Retained official reference: books 13753–13848,
// candles 14012–14116 (bar=1Dutc, max300, confirm index8). No signing/provider.
internal sealed class OkxPublicMarketFeed : IDisposable
{
    private readonly HttpClient http;
    private readonly TimeProvider clock;
    internal OkxPublicMarketFeed(TimeProvider clock,HttpMessageHandler? handler=null)
    {
        this.clock=clock;http=new(handler??new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false},handler==null){BaseAddress=new Uri("https://eea.okx.com"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private async Task<JsonDocument> Get(string path,CancellationToken ct)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,path);
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
        if(!response.IsSuccessStatusCode)throw new InvalidDataException("PUBLIC_FEED_HTTP");
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=await stream.ReadAsync(buffer,ct).ConfigureAwait(false))!=0){if(bytes.Length+n>1048576)throw new InvalidDataException("PUBLIC_FEED_LIMIT");bytes.Write(buffer,0,n);}
        var doc=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=16});
        try{Unique(doc.RootElement);if(doc.RootElement.GetProperty("code").GetString()!="0"||doc.RootElement.GetProperty("data").ValueKind!=JsonValueKind.Array)throw new InvalidDataException("PUBLIC_FEED_PAYLOAD");return doc;}catch{doc.Dispose();throw;}
    }
    private static void Unique(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in e.EnumerateObject()){if(!names.Add(p.Name))throw new InvalidDataException("PUBLIC_FEED_DUPLICATE");Unique(p.Value);}}
        else if(e.ValueKind==JsonValueKind.Array)foreach(var v in e.EnumerateArray())Unique(v);
    }
    private static decimal N(JsonElement e)
    {
        string raw=e.GetString()??throw new InvalidDataException("PUBLIC_FEED_NUMBER");
        if(!System.Text.RegularExpressions.Regex.IsMatch(raw,"\\A(?:0|[1-9][0-9]{0,27})(?:\\.[0-9]{1,28})?\\z")||!decimal.TryParse(raw,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value)||raw.Replace(".","").TrimStart('0').TrimEnd('0').Length>28)throw new InvalidDataException("PUBLIC_FEED_NUMBER");return value;
    }
    private static string Instrument(NavalInstrument i)
    {if(i.Instrument!=i.BaseCurrency+"-"+i.QuoteCurrency||!System.Text.RegularExpressions.Regex.IsMatch(i.Instrument,"\\A[A-Z0-9]{1,16}-[A-Z0-9]{2,12}\\z"))throw new InvalidDataException("PUBLIC_FEED_INSTRUMENT");return i.Instrument;}
    internal async Task<NavalBrokerQuote> ReadQuoteAsync(NavalInstrument i,CancellationToken ct)
    {
        using var doc=await Get("/api/v5/market/books?instId="+Instrument(i)+"&sz=1",ct).ConfigureAwait(false);
        var data=doc.RootElement.GetProperty("data");if(data.GetArrayLength()!=1)throw new InvalidDataException("PUBLIC_BOOK_CARDINALITY");var row=data[0];
        var ask=row.GetProperty("asks");var bid=row.GetProperty("bids");if(ask.GetArrayLength()!=1||bid.GetArrayLength()!=1||ask[0].GetArrayLength()!=4||bid[0].GetArrayLength()!=4)throw new InvalidDataException("PUBLIC_BOOK_DEPTH");
        var source=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row.GetProperty("ts").GetString()!,CultureInfo.InvariantCulture));
        return new(i.Instrument,N(bid[0][0]),N(ask[0][0]),N(ask[0][1]),source,clock.GetUtcNow(),clock.GetTimestamp());
    }
    internal async Task<NavalSeries> ReadSeriesAsync(NavalInstrument i,CancellationToken ct)
    {
        using var doc=await Get("/api/v5/market/candles?bar=1Dutc&instId="+Instrument(i)+"&limit=300",ct).ConfigureAwait(false);
        var rows=doc.RootElement.GetProperty("data");if(rows.GetArrayLength()>300)throw new InvalidDataException("PUBLIC_CANDLE_LIMIT");
        var bars=new List<NavalBar>();DateTimeOffset? previous=null;var cutoff=new DateTimeOffset(clock.GetUtcNow().UtcDateTime.Date,TimeSpan.Zero);
        foreach(var row in rows.EnumerateArray())
        {
            if(row.GetArrayLength()!=9)throw new InvalidDataException("PUBLIC_CANDLE_SHAPE");
            var time=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(row[0].GetString()!,CultureInfo.InvariantCulture));
            if(previous!=null&&time>=previous)throw new InvalidDataException("PUBLIC_CANDLE_ORDER");previous=time;
            string confirm=row[8].GetString()!;if(confirm is not ("0" or "1"))throw new InvalidDataException("PUBLIC_CANDLE_CONFIRM");
            if(time>=cutoff)continue;
            if(confirm!="1")throw new InvalidDataException("PUBLIC_CANDLE_UNCONFIRMED");
            bars.Add(new(time,N(row[1]),N(row[2]),N(row[3]),N(row[4]),N(row[5]),true));
        }
        bars.Reverse();return new(i.Instrument,i.BaseCurrency,i.QuoteCurrency,"OKX EEA public /market/candles 1Dutc","trade",bars);
    }
    public void Dispose()=>http.Dispose();
}
