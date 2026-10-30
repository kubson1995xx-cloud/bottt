using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apex.Core.Naval;
namespace Apex.Backtester.Naval;

// Rebuildable, sanitized historical projection; this class never acquires an authority owner.
internal static class SharedPublicationExport
{
    internal static readonly JsonSerializerOptions Json=new()
    {
        PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,
        Converters={new DecimalString(),new CounterString(),new JsonStringEnumConverter(null,false)}
    };
    internal static void Write(string path,NavalReadPublication publication,string inputSha256,string coreSha256,string backtesterSha256,DateTimeOffset sourceClockUtc)
    {
        if(publication==null)throw new InvalidOperationException("PRODUCER_NOT_INITIALIZED");
        foreach(string hash in new[]{inputSha256,coreSha256,backtesterSha256})
            if(hash.Length!=64||!System.Linq.Enumerable.All(hash,Uri.IsHexDigit))throw new ArgumentException("Invalid provenance hash");
        var page=NavalReadJournal.ReadPage(publication,new(publication.Journal.Epoch,publication.Journal.ScopeId,publication.AsOfSequence));
        if(page.ResetRequired)throw new InvalidDataException("Invalid committed publication");
        var content=new SharedPublicationContent("naval-shared-publication-export-v1","HISTORICAL_OFFLINE_CHECKPOINT",sourceClockUtc,
            "UNKNOWN",new(inputSha256,coreSha256,backtesterSha256),publication);
        string digest=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content,Json))).ToLowerInvariant();
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new SharedPublicationEnvelope(content.Schema,content.TemporalClassification,content.SourceClockUtc,
            content.SourceFreshness,content.Provenance,content.Publication,digest),Json);
        path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+".candidate-"+Guid.NewGuid().ToString("N");
        try
        {
            var options=new FileStreamOptions{Mode=FileMode.CreateNew,Access=FileAccess.Write,Share=FileShare.None,Options=FileOptions.WriteThrough};
            if(!OperatingSystem.IsWindows())options.UnixCreateMode=UnixFileMode.UserRead|UnixFileMode.UserWrite;
            using(var stream=new FileStream(temp,options))
            {stream.Write(bytes);stream.Flush(true);}
            File.Move(temp,path,true); // Same-directory rename: readers see one complete generation.
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    internal sealed record SharedPublicationProvenance(string InputSha256,string CoreSha256,string BacktesterSha256);
    internal sealed record SharedPublicationContent(string Schema,string TemporalClassification,DateTimeOffset SourceClockUtc,string SourceFreshness,
        SharedPublicationProvenance Provenance,NavalReadPublication Publication);
    internal sealed record SharedPublicationEnvelope(string Schema,string TemporalClassification,DateTimeOffset SourceClockUtc,string SourceFreshness,
        SharedPublicationProvenance Provenance,NavalReadPublication Publication,string ContentSha256);
    private sealed class CounterString:JsonConverter<long>
    {
        public override long Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType==JsonTokenType.String&&
            long.TryParse(reader.GetString(),NumberStyles.None,CultureInfo.InvariantCulture,out long value)?value:throw new JsonException("Decimal string counter required");
        public override void Write(Utf8JsonWriter writer,long value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
    private sealed class DecimalString:JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader,Type type,JsonSerializerOptions options)=>reader.TokenType==JsonTokenType.String&&
            decimal.TryParse(reader.GetString(),NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out decimal value)?value:throw new JsonException("Invariant decimal string required");
        public override void Write(Utf8JsonWriter writer,decimal value,JsonSerializerOptions options)=>writer.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
    }
}
