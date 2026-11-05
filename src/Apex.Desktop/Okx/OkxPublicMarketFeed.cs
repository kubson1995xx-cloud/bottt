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
    private readonly Dictionary<string,DateTimeOffset> quoteSources=new(StringComparer.Ordinal);
    // Release-owned metadata only. No query, provider text, exception or body.
    internal OkxPublicFeedDiagnostic? LastDiagnostic { get; private set; }
    internal OkxPublicMarketFeed(TimeProvider clock,HttpMessageHandler? handler=null)
    {
        this.clock=clock;http=new(handler??new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false},handler==null){BaseAddress=new Uri("https://eea.okx.com"),Timeout=TimeSpan.FromSeconds(10)};
    }
    private async Task<JsonDocument> Get(string path,CancellationToken ct)
    {
        LastDiagnostic=new(path.Split('?')[0],"REQUEST_STARTED",null,null);
        try
        {
        using var request=new HttpRequestMessage(HttpMethod.Get,path);
        request.Headers.Add("x-simulated-trading","1");
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct).ConfigureAwait(false);
        LastDiagnostic=LastDiagnostic with{HttpStatus=(int)response.StatusCode};
        if(!response.IsSuccessStatusCode){LastDiagnostic=LastDiagnostic with{Reason="PUBLIC_HTTP_REJECTED"};throw new InvalidDataException("PUBLIC_FEED_HTTP");}
        await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
        while((n=await stream.ReadAsync(buffer,ct).ConfigureAwait(false))!=0){if(bytes.Length+n>1048576)throw new InvalidDataException("PUBLIC_FEED_LIMIT");bytes.Write(buffer,0,n);}
        var doc=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=16});
        try
        {
            Unique(doc.RootElement);
            string? code=doc.RootElement.GetProperty("code").GetString();
            if(code!=null&&System.Text.RegularExpressions.Regex.IsMatch(code,"\\A(?:0|[1-9][0-9]{0,8})\\z")&&int.TryParse(code,NumberStyles.None,CultureInfo.InvariantCulture,out int numeric))LastDiagnostic=LastDiagnostic with{Code=numeric};
            if(code!="0"){LastDiagnostic=LastDiagnostic with{Reason=LastDiagnostic.Code!=null?"PUBLIC_PROVIDER_REJECTED":"PUBLIC_PAYLOAD_INVALID"};throw new InvalidDataException("PUBLIC_FEED_PAYLOAD");}
            if(doc.RootElement.GetProperty("data").ValueKind!=JsonValueKind.Array)throw new InvalidDataException("PUBLIC_FEED_PAYLOAD");
            LastDiagnostic=LastDiagnostic with{Reason="PAYLOAD_OBSERVED"};return doc;
        }
        catch{doc.Dispose();throw;}
        }
        catch(OperationCanceledException){LastDiagnostic=LastDiagnostic with{Reason="CANCELLED"};throw;}
        catch(HttpRequestException){LastDiagnostic=LastDiagnostic with{Reason="PUBLIC_TRANSPORT_FAILURE"};throw;}
        catch{if(LastDiagnostic.Reason=="REQUEST_STARTED")LastDiagnostic=LastDiagnostic with{Reason="PUBLIC_PAYLOAD_INVALID"};throw;}
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
    internal Task<NavalBrokerQuote> ReadQuoteAsync(NavalInstrument i,CancellationToken ct)=>ReadQuoteCoreAsync(i,ct,false);
    // Availability nomination only; execution/reconciliation retain strict ReadQuoteAsync.
    internal Task<NavalBrokerQuote> ReadFiniteQuoteAsync(NavalInstrument i,CancellationToken ct)=>ReadQuoteCoreAsync(i,ct,true);
    private async Task<NavalBrokerQuote> ReadQuoteCoreAsync(NavalInstrument i,CancellationToken ct,bool finite)
    {
        string instrument;
        try{instrument=Instrument(i);}
        catch{LastDiagnostic=new("/api/v5/market/books","PUBLIC_INSTRUMENT_INVALID",null,null);throw;}
        var requested=clock.GetUtcNow();
        try
        {
            using var doc=await Get("/api/v5/market/books?instId="+instrument+"&sz=1",ct).ConfigureAwait(false);
            LastDiagnostic=LastDiagnostic! with{Instrument=instrument};
            var parser=new BookParser(this,requested,clock.GetUtcNow());
            var data=doc.RootElement.GetProperty("data");parser.Counts(data);
            parser.Count(data,1,"$.data","BOOK_CARDINALITY");var row=data[0];parser.Kind(row,JsonValueKind.Object,"$.data[0]");
            row.TryGetProperty("asks",out var observedAsk);row.TryGetProperty("bids",out var observedBid);parser.Counts(data,observedAsk,observedBid);
            var ask=parser.Property(row,"asks","$.data[0].asks");var bid=parser.Property(row,"bids","$.data[0].bids");
            parser.Kind(ask,JsonValueKind.Array,"$.data[0].asks");parser.Kind(bid,JsonValueKind.Array,"$.data[0].bids");
            if(finite&&(ask.GetArrayLength()==0||bid.GetArrayLength()==0))
            {
                var unavailable=parser.EmptySide(doc.RootElement,row,ask,bid,instrument,clock.GetTimestamp());
                if(quoteSources.TryGetValue(instrument,out var previousEmpty)&&unavailable.SourceUtc<previousEmpty)parser.Regression(unavailable.SourceUtc,previousEmpty);
                ct.ThrowIfCancellationRequested();quoteSources[instrument]=unavailable.SourceUtc;throw unavailable;
            }
            parser.Count(ask,1,"$.data[0].asks","BOOK_DEPTH");parser.Count(bid,1,"$.data[0].bids","BOOK_DEPTH");
            parser.Kind(ask[0],JsonValueKind.Array,"$.data[0].asks[0]");parser.Kind(bid[0],JsonValueKind.Array,"$.data[0].bids[0]");
            parser.Count(ask[0],4,"$.data[0].asks[0]","BOOK_FIELDS");parser.Count(bid[0],4,"$.data[0].bids[0]","BOOK_FIELDS");
            var ts=parser.Property(row,"ts","$.data[0].ts");parser.Kind(ts,JsonValueKind.String,"$.data[0].ts");
            var source=parser.Timestamp(ts);
            if(quoteSources.TryGetValue(i.Instrument,out var previous)&&source<previous)parser.Regression(source,previous);
            quoteSources[i.Instrument]=source; // Preserve existing monotonic cache and evaluation order.
            return new(i.Instrument,parser.Number(bid[0][0],"$.data[0].bids[0][0]"),parser.Number(ask[0][0],"$.data[0].asks[0][0]"),parser.Number(ask[0][1],"$.data[0].asks[0][1]"),source,clock.GetUtcNow(),clock.GetTimestamp());
        }
        catch{if(LastDiagnostic!=null)LastDiagnostic=LastDiagnostic with{Instrument=instrument};throw;}
    }
    // Projection owns only fixed paths/vocabulary, bounded counts and lexical metadata.
    // It never copies arbitrary property names, numerical values, body or exception text.
    private sealed class BookParser
    {
        private readonly OkxPublicMarketFeed feed;
        private OkxPublicQuoteDiagnostic state;
        internal BookParser(OkxPublicMarketFeed feed,DateTimeOffset requested,DateTimeOffset received)
        {this.feed=feed;state=new("PARSER_STARTED","$.data","Array","Unknown"){RequestUtc=requested,ReceivedUtc=received};}
        private void Fail(string reason,string path,string expected,string actual,int? expectedCount=null,int? actualCount=null)
        {
            feed.LastDiagnostic=feed.LastDiagnostic! with{QuoteParser=state with{Reason=reason,Path=path,ExpectedKind=expected,ActualKind=actual,ExpectedCount=expectedCount,ActualCount=actualCount}};
            throw new InvalidDataException(reason.StartsWith("NUMBER",StringComparison.Ordinal)?"PUBLIC_FEED_NUMBER":reason=="QUOTE_SOURCE_REGRESSION"?"QUOTE_SOURCE_REGRESSION":reason=="BOOK_CARDINALITY"?"PUBLIC_BOOK_CARDINALITY":"PUBLIC_BOOK_DEPTH");
        }
        internal OkxBookSideUnavailable EmptySide(JsonElement root,JsonElement row,JsonElement asks,JsonElement bids,string instrument,long receivedTimestamp)
        {
            // A zero side is known local unavailability only after the complete
            // pinned success shape and every present opposite-side operand pass.
            if(feed.LastDiagnostic is not {HttpStatus:200,Code:0,Reason:"PAYLOAD_OBSERVED"})Fail("BOOK_SUCCESS_UNPROVEN","$","Success200","Unknown");
            foreach(var p in root.EnumerateObject())if(p.Name is not ("code" or "msg" or "data"))Fail("PROPERTY_UNSUPPORTED","$.<unknown>","Documented","Unknown");
            if(root.TryGetProperty("msg",out var msg)){Kind(msg,JsonValueKind.String,"$.msg");if(msg.GetString()!="")Fail("BOOK_SUCCESS_UNPROVEN","$.msg","EmptyString","String");}
            foreach(var p in row.EnumerateObject())if(p.Name is not ("asks" or "bids" or "ts" or "seqId"))Fail("PROPERTY_UNSUPPORTED","$.data[0].<unknown>","Documented","Unknown");
            if(row.TryGetProperty("seqId",out var sequence))
            {Kind(sequence,JsonValueKind.Number,"$.data[0].seqId");if(!sequence.TryGetInt64(out _))Fail("BOOK_METADATA","$.data[0].seqId","Integer","Number");}
            foreach(var side in new[]{(asks,"$.data[0].asks"),(bids,"$.data[0].bids")})
            {
                if(side.Item1.GetArrayLength()>1)Fail("BOOK_DEPTH",side.Item2,"Array","Array",1,side.Item1.GetArrayLength());
                if(side.Item1.GetArrayLength()==0)continue;
                var level=side.Item1[0];string path=side.Item2+"[0]";Kind(level,JsonValueKind.Array,path);Count(level,4,path,"BOOK_FIELDS");
                if(Number(level[0],path+"[0]")<=0)Fail("NUMBER_NONPOSITIVE",path+"[0]","PositiveString","String");
                if(Number(level[1],path+"[1]")<=0)Fail("NUMBER_NONPOSITIVE",path+"[1]","PositiveString","String");
                Kind(level[2],JsonValueKind.String,path+"[2]");if(level[2].GetString()!="0")Fail("BOOK_METADATA",path+"[2]","ZeroString","String");
                Kind(level[3],JsonValueKind.String,path+"[3]");string orders=level[3].GetString()!;
                if(!System.Text.RegularExpressions.Regex.IsMatch(orders,"\\A[1-9][0-9]{0,18}\\z")||!long.TryParse(orders,NumberStyles.None,CultureInfo.InvariantCulture,out _))Fail("BOOK_METADATA",path+"[3]","PositiveIntegerString","String");
            }
            var ts=Property(row,"ts","$.data[0].ts");Kind(ts,JsonValueKind.String,"$.data[0].ts");
            if(!System.Text.RegularExpressions.Regex.IsMatch(ts.GetString()!,"\\A(?:0|[1-9][0-9]{0,18})\\z"))Fail("TIMESTAMP_FORMAT","$.data[0].ts","MillisecondsString","String");
            var source=Timestamp(ts);var unavailable=new OkxBookSideUnavailable(instrument,source,state.RequestUtc,state.ReceivedUtc,receivedTimestamp,asks.GetArrayLength(),bids.GetArrayLength());
            if(!unavailable.IsCurrent(instrument,feed.clock))Fail("QUOTE_TIME_UNPROVEN","$.data[0].ts","FreshSource","String");
            feed.LastDiagnostic=feed.LastDiagnostic! with{QuoteParser=state with{Reason="BOOK_SIDE_UNAVAILABLE",Path=asks.GetArrayLength()==0?"$.data[0].asks":"$.data[0].bids",ExpectedKind="Array",ActualKind="Array",ExpectedCount=1,ActualCount=0}};
            return unavailable;
        }
        internal void Kind(JsonElement e,JsonValueKind expected,string path)
        {if(e.ValueKind!=expected)Fail("JSON_KIND",path,expected.ToString(),e.ValueKind.ToString());}
        internal JsonElement Property(JsonElement row,string name,string path)
        {if(!row.TryGetProperty(name,out var e))Fail("PROPERTY_MISSING",path,name=="ts"?"String":"Array","Missing");return e;}
        internal void Count(JsonElement e,int expected,string path,string reason)
        {Kind(e,JsonValueKind.Array,path);if(e.GetArrayLength()!=expected)Fail(reason,path,"Array","Array",expected,e.GetArrayLength());}
        private static int? Length(JsonElement e)=>e.ValueKind==JsonValueKind.Array?e.GetArrayLength():null;
        private static int? Fields(JsonElement e)=>Length(e)>0?Length(e[0]):null;
        internal void Counts(JsonElement data,JsonElement asks=default,JsonElement bids=default)
        {state=state with{DataRows=Length(data),AskRows=Length(asks),BidRows=Length(bids),AskFields=Fields(asks),BidFields=Fields(bids)};}
        private void Lexical(string raw)
        {
            int dot=raw.IndexOf('.');
            string category=raw.Length==0?"EMPTY":raw.Any(char.IsWhiteSpace)?"WHITESPACE":raw[0] is '+' or '-'?"SIGNED":
                raw.Any(c=>c is 'e' or 'E')&&raw.All(c=>char.IsAsciiDigit(c)||c is '.' or 'e' or 'E' or '+' or '-')?"EXPONENT":
                raw.Any(c=>!char.IsAsciiDigit(c)&&c!='.')?"INVALID_CHARACTERS":raw.Count(c=>c=='.')>1?"MULTIPLE_DECIMAL_POINTS":
                raw.Length>1&&raw[0]=='0'&&raw[1]!='.'?"LEADING_ZERO":dot<0?"INTEGER":"PLAIN_DECIMAL";
            state=state with{NumberClass=category,CharacterCount=raw.Length,IntegerDigits=dot<0?raw.Count(char.IsAsciiDigit):raw.Take(dot).Count(char.IsAsciiDigit),FractionDigits=dot<0?0:raw.Skip(dot+1).Count(char.IsAsciiDigit),SignificantDigits=raw.Replace(".","").TrimStart('0').TrimEnd('0').Length};
        }
        internal DateTimeOffset Timestamp(JsonElement e)
        {
            string raw=e.GetString()!;Lexical(raw);long milliseconds;
            // Preserve the existing long.Parse culture/style and accepted timestamp domain.
            try{milliseconds=long.Parse(raw,CultureInfo.InvariantCulture);}
            catch(OverflowException){Fail("TIMESTAMP_OVERFLOW","$.data[0].ts","String","String");throw;}
            catch(FormatException){Fail("TIMESTAMP_FORMAT","$.data[0].ts","String","String");throw;}
            DateTimeOffset source;
            try{source=DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);}
            catch(ArgumentOutOfRangeException){Fail("TIMESTAMP_RANGE","$.data[0].ts","String","String");throw;}
            state=state with{SourceUtc=source,NumberClass=null,CharacterCount=null,IntegerDigits=null,FractionDigits=null,SignificantDigits=null};return source;
        }
        internal void Regression(DateTimeOffset source,DateTimeOffset previous)
        {state=state with{SourceUtc=source,PreviousSourceUtc=previous};Fail("QUOTE_SOURCE_REGRESSION","$.data[0].ts","String","String");}
        internal decimal Number(JsonElement e,string path)
        {
            if(e.ValueKind==JsonValueKind.Null)Fail("NUMBER_NULL",path,"String","Null");
            Kind(e,JsonValueKind.String,path);string raw=e.GetString()!;Lexical(raw);
            // Identical grammar, decimal parser and significant-digit guard to N; no rounding/coercion fallback.
            if(!System.Text.RegularExpressions.Regex.IsMatch(raw,"\\A(?:0|[1-9][0-9]{0,27})(?:\\.[0-9]{1,28})?\\z"))Fail("NUMBER_LEXICAL",path,"String","String");
            if(!decimal.TryParse(raw,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var value))Fail("NUMBER_RANGE",path,"String","String");
            if(raw.Replace(".","").TrimStart('0').TrimEnd('0').Length>28)Fail("NUMBER_PRECISION",path,"String","String");
            state=state with{NumberClass=null,CharacterCount=null,IntegerDigits=null,FractionDigits=null,SignificantDigits=null};return value;
        }
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
    internal async Task<NavalBrokerQuote> ReadUsdIndexAsync(NavalInstrument i,CancellationToken ct)
    {
        string index=i.BaseCurrency+"-USD";
        using var doc=await Get("/api/v5/market/index-tickers?instId="+index,ct).ConfigureAwait(false);
        var rows=doc.RootElement.GetProperty("data");if(rows.GetArrayLength()!=1||rows[0].GetProperty("instId").GetString()!=index)throw new InvalidDataException("USD_INDEX_UNPROVEN");
        var r=rows[0];decimal price=N(r.GetProperty("idxPx"));var source=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(r.GetProperty("ts").GetString()!,CultureInfo.InvariantCulture));
        if(price<=0||source>clock.GetUtcNow()||clock.GetUtcNow()-source>=TimeSpan.FromSeconds(5))throw new InvalidDataException("USD_INDEX_STALE");
        // Reference valuation only, never used as an executable bid/ask or a USD/USDC peg.
        return new(index,price,price,1,source,clock.GetUtcNow(),clock.GetTimestamp());
    }
    internal async Task<OkxDemoPriceBand> ReadPriceBandAsync(NavalInstrument i,CancellationToken ct)
    {
        using var doc=await Get("/api/v5/public/price-limit?instId="+Instrument(i),ct).ConfigureAwait(false);
        var rows=doc.RootElement.GetProperty("data");if(rows.GetArrayLength()!=1)throw new InvalidDataException("PRICE_BAND_UNPROVEN");var r=rows[0];
        if(r.GetProperty("instId").GetString()!=i.Instrument||r.GetProperty("instType").GetString()!="SPOT"||r.GetProperty("enabled").ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("PRICE_BAND_UNPROVEN");
        var source=DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(r.GetProperty("ts").GetString()!,CultureInfo.InvariantCulture));
        if(source>clock.GetUtcNow()||clock.GetUtcNow()-source>=TimeSpan.FromSeconds(5))throw new InvalidDataException("PRICE_BAND_STALE");
        bool enabled=r.GetProperty("enabled").GetBoolean();
        return new(i.Instrument,enabled,enabled?N(r.GetProperty("buyLmt")):null,enabled?N(r.GetProperty("sellLmt")):null,source);
    }
}
internal sealed class OkxBookSideUnavailable(string instrument,DateTimeOffset source,DateTimeOffset requested,DateTimeOffset received,long receivedTimestamp,int askRows,int bidRows):InvalidOperationException("FINITE_BOOK_SIDE_UNAVAILABLE")
{
    internal string Instrument=>instrument;
    internal DateTimeOffset SourceUtc=>source;
    internal OkxFiniteUnavailableBook Attempt=>new(instrument,"FINITE_BOOK_SIDE_UNAVAILABLE",askRows,bidRows,source);
    internal bool IsCurrent(string expected,TimeProvider clock)=>instrument==expected&&(askRows==0||bidRows==0)&&askRows is >=0 and <=1&&bidRows is >=0 and <=1&&
        requested<=received&&source<=received&&received<=clock.GetUtcNow()&&clock.GetUtcNow()-source<TimeSpan.FromSeconds(5)&&receivedTimestamp<=clock.GetTimestamp();
}
internal sealed record OkxDemoPriceBand(string Instrument,bool Enabled,decimal? BuyMaximum,decimal? SellMinimum,DateTimeOffset SourceUtc);
internal sealed record OkxPublicFeedDiagnostic(string Endpoint,string Reason,int? HttpStatus,int? Code)
{
    public string? Instrument { get; init; }
    public OkxPublicQuoteDiagnostic? QuoteParser { get; init; }
}
internal sealed record OkxPublicQuoteDiagnostic(string Reason,string Path,string ExpectedKind,string ActualKind)
{
    public int? ExpectedCount { get; init; }
    public int? ActualCount { get; init; }
    public int? DataRows { get; init; }
    public int? AskRows { get; init; }
    public int? BidRows { get; init; }
    public int? AskFields { get; init; }
    public int? BidFields { get; init; }
    public string? NumberClass { get; init; }
    public int? CharacterCount { get; init; }
    public int? IntegerDigits { get; init; }
    public int? FractionDigits { get; init; }
    public int? SignificantDigits { get; init; }
    public DateTimeOffset RequestUtc { get; init; }
    public DateTimeOffset ReceivedUtc { get; init; }
    public DateTimeOffset? SourceUtc { get; init; }
    public DateTimeOffset? PreviousSourceUtc { get; init; }
}
