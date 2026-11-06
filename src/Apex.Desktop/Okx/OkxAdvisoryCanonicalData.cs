using System;
using System.IO;
using System.Globalization;
using System.Text.Json;
using Apex.Core.Naval;
namespace Apex.Desktop.Okx;

// Pure export of the actual consumed series, no change to feed/strategy behavior.
internal static class OkxAdvisoryCanonicalData
{
    internal static byte[] Serialize(NavalSeries series, DateTimeOffset cutoff)
    {
        if(series.Instrument is not ("BTC-EUR" or "ETH-EUR" or "SOL-EUR") ||
           series.Instrument!=series.BaseAsset+"-EUR" || series.QuoteAsset!="EUR" ||
           series.Source!="OKX EEA public /market/candles 1Dutc" || series.PriceBasis!="trade" ||
           cutoff.Offset!=TimeSpan.Zero || cutoff.TimeOfDay!=TimeSpan.Zero || series.Bars.Count is <200 or >300)
            throw new InvalidDataException("ADVISORY_DATA_REFUSED");
        using var stream=new MemoryStream();
        using(var w=new Utf8JsonWriter(stream))
        {
            w.WriteStartObject();w.WriteString("Schema","apex-strategy-candles-v1");w.WriteString("Instrument",series.Instrument);
            w.WriteString("BaseCurrency",series.BaseAsset);w.WriteString("QuoteCurrency","EUR");w.WriteString("Source",series.Source);
            w.WriteString("Bar","1Dutc");w.WriteString("PriceBasis","trade");w.WriteString("VolumeUnit",series.BaseAsset);
            w.WriteString("AsOfUtc",cutoff.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'",CultureInfo.InvariantCulture));w.WriteStartArray("Bars");
            DateTimeOffset? previous=null;
            foreach(var b in series.Bars)
            {
                if(!b.Confirmed || b.OpenUtc.Offset!=TimeSpan.Zero || b.OpenUtc.TimeOfDay!=TimeSpan.Zero ||
                   b.OpenUtc.AddDays(1)>cutoff || (previous!=null && b.OpenUtc!=previous.Value.AddDays(1)) ||
                   b.Open<=0 || b.High<=0 || b.Low<=0 || b.Close<=0 || b.Volume<0 || b.Low>b.Open || b.Open>b.High || b.Low>b.Close || b.Close>b.High)
                    throw new InvalidDataException("ADVISORY_BAR_REFUSED");
                previous=b.OpenUtc;w.WriteStartArray();w.WriteStringValue(b.OpenUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                foreach(decimal n in new[]{b.Open,b.High,b.Low,b.Close,b.Volume})w.WriteStringValue(n.ToString("0.############################",CultureInfo.InvariantCulture));
                w.WriteStringValue("1");w.WriteEndArray();
            }
            if(previous?.AddDays(1)!=cutoff)throw new InvalidDataException("ADVISORY_LATEST_REFUSED");
            w.WriteEndArray();w.WriteEndObject();
        }
        return stream.ToArray();
    }
}
