using System.Security.Cryptography;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

// Closed numeric diagnostics only. This is NEVER proof of rejection or permission
// to release a reservation. Venue messages/headers/private payloads are discarded.
public sealed record OkxHttpDiagnostic(string Endpoint,int HttpStatus,string? Code,string? SCode,string? SubCode);
internal sealed class OkxHttpDiagnosticCapture
{
    private readonly List<OkxHttpDiagnostic> rows=[];
    internal IReadOnlyList<OkxHttpDiagnostic> Snapshot(){lock(rows)return rows.ToArray();}
    internal void Add(string endpoint,int status,ReadOnlyMemory<byte> bytes=default)
    {
        string? code=null,sub=null,item=null;
        try
        {
            using var doc=JsonDocument.Parse(bytes);Grammar.Unique(doc.RootElement);var root=doc.RootElement;
            string? Numeric(JsonElement e,string key)=>e.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String&&Grammar.Match(p.GetString(),"(?:0|1|2|[1-9][0-9]{4,7})")?p.GetString():null;
            code=Numeric(root,"code");
            if(root.TryGetProperty("data",out var data)&&data.ValueKind==JsonValueKind.Array&&data.GetArrayLength()==1&&data[0].ValueKind==JsonValueKind.Object){item=Numeric(data[0],"sCode");sub=Numeric(data[0],"subCode");}
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or ArgumentException){code=null;item=null;sub=null;}
        lock(rows){if(rows.Count<2048)rows.Add(new(endpoint,status,code,item,sub));}
    }
    internal async Task ErrorAsync(string endpoint,HttpResponseMessage response,CancellationToken ct)
    {
        const int cap=65536;byte[] bytes=new byte[cap+1];int used=0;
        try
        {
            if((int)response.StatusCode is >=300 and <=399||response.Content.Headers.ContentLength>cap)return;
            await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            int n;while((n=await stream.ReadAsync(bytes.AsMemory(used,bytes.Length-used),ct).ConfigureAwait(false))>0){used+=n;if(used>cap){used=0;break;}}
        }
        catch(Exception e) when(e is IOException or HttpRequestException or OperationCanceledException){used=0;}
        finally{Add(endpoint,(int)response.StatusCode,bytes.AsMemory(0,used));CryptographicOperations.ZeroMemory(bytes);}
    }
}
