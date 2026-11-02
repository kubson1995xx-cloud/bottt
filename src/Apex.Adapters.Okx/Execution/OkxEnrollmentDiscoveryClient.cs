using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Apex.Adapters.Okx.Execution;

// Observation-only identity; deliberately has no quote, execution scope or authority.
public sealed record OkxEnrollmentObservation(string Uid,string MainUid,string Environment,string Provenance,
    string Fingerprint,DateTimeOffset ObservedUtc,ImmutableArray<OkxExecutionBalanceDetailDto> Balances,
    ImmutableArray<OkxExecutionInstrumentDto> Instruments);
// Only closed request targets, HTTP integers and numeric OKX codes reach UI/status.
// No venue messages, response text, headers or account identifiers are diagnostics.
public sealed record OkxEnrollmentDiagnostic(string Endpoint,int HttpStatus,string? OkxCode)
{
    internal string Suffix=>$" [GET {Endpoint}; HTTP {HttpStatus}; OKX {OkxCode??"UNKNOWN"}]";
}
public sealed record OkxEnrollmentDiscovery(OkxEnrollmentObservation? Observation,string Code,OkxEnrollmentDiagnostic? Diagnostic=null);
public static class OkxEnrollmentDiscoveryClient
{
    public static Task<OkxEnrollmentDiscovery> DiscoverAsync(string environment,string provenance,ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,string? expectedUid,string? expectedMainUid,CancellationToken ct)
        =>RunAsync(environment,provenance,key,secret,passphrase,expectedUid,expectedMainUid,ct,null);
    // Old UID-bound observations and unknown-UID bootstrap use this same gate.
    // Bootstrap holds it for the entire four-GET pass with 600ms guards around
    // private sends; no credential alias can bypass an account quota before UID.
    internal static readonly SemaphoreSlim BootstrapInterlock=new(1,1);
    private static readonly SemaphoreSlim readersGate=new(1,1);
    private static int readers;
    internal static async Task EnterOrdinaryAsync(CancellationToken ct)
    {
        await readersGate.WaitAsync(ct).ConfigureAwait(false);
        try{if(readers==0)await BootstrapInterlock.WaitAsync(ct).ConfigureAwait(false);readers++;}
        finally{readersGate.Release();}
    }
    internal static void ExitOrdinary()
    {
        readersGate.Wait();try{if(--readers==0)BootstrapInterlock.Release();}finally{readersGate.Release();}
    }
    internal static async Task<OkxEnrollmentDiscovery> RunAsync(string environment,string provenance,ReadOnlyMemory<byte> key,
        ReadOnlyMemory<byte> secret,ReadOnlyMemory<byte> passphrase,string? expectedUid,string? expectedMainUid,CancellationToken caller,HttpMessageHandler? handler)
    {
        if(environment!="Demo")return new(null,"DISCOVERY_DEMO_ONLY");
        if(provenance!="DemoTradingApi")return new(null,"DISCOVERY_PROVENANCE_MISMATCH");
        if(!Header(key.Span)||!Header(passphrase.Span)||secret.Length is <=0 or >4096)return new(null,"DISCOVERY_CREDENTIAL_INVALID");
        if((expectedUid==null)!=(expectedMainUid==null))return new(null,"DISCOVERY_IDENTITY_MISMATCH");
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(caller);bounded.CancelAfter(TimeSpan.FromSeconds(35));var ct=bounded.Token;
        var start=DateTimeOffset.UtcNow;long mono=TimeProvider.System.GetTimestamp();bool held=false;byte[]? k=null,s=null,p=null;
        bool Valid()=>!ct.IsCancellationRequested&&!ExecutionDiagnostics.Unsafe&&DateTimeOffset.UtcNow>=start&&DateTimeOffset.UtcNow-start<TimeSpan.FromSeconds(35)&&TimeProvider.System.GetElapsedTime(mono)<TimeSpan.FromSeconds(35);
        void Check(){if(!Valid())throw new OperationCanceledException(ct);}
        try
        {
            Check();k=key.ToArray();s=secret.ToArray();p=passphrase.ToArray();
            using var http=new HttpMessageInvoker(handler??new SocketsHttpHandler{AllowAutoRedirect=false,UseProxy=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,ConnectTimeout=TimeSpan.FromSeconds(5),ActivityHeadersPropagator=null},true);
            await BootstrapInterlock.WaitAsync(ct).ConfigureAwait(false);held=true;Check();
            async Task<IReadOnlyList<T>> Get<T>(string endpoint,string query="",bool authenticated=true)
            {
                // Serialize conservatively even when a different existing key owns
                // an in-flight request. Both clocks bound every wait and final send.
                if(authenticated)await Task.Delay(TimeSpan.FromMilliseconds(610),ct).ConfigureAwait(false);
                Check();string exact=endpoint+query;
                using var request=new HttpRequestMessage(HttpMethod.Get,"https://eea.okx.com:443"+exact);
                request.Headers.Add("x-simulated-trading","1");
                if(authenticated)
                {
                    string stamp=OkxRequestSigner.Timestamp(DateTimeOffset.UtcNow);
                    request.Headers.Add("OK-ACCESS-KEY",Encoding.ASCII.GetString(k));request.Headers.Add("OK-ACCESS-PASSPHRASE",Encoding.ASCII.GetString(p));
                    request.Headers.Add("OK-ACCESS-TIMESTAMP",stamp);request.Headers.Add("OK-ACCESS-SIGN",OkxRequestSigner.Sign(s,stamp,"GET",exact,ReadOnlySpan<byte>.Empty));
                }
                Check(); // final fence after signing, immediately before invocation
                using var response=await http.SendAsync(request,ct).ConfigureAwait(false);Check();
                const int cap=1024*1024;byte[] bytes=new byte[cap+1];
                bool refused=!response.IsSuccessStatusCode;
                DiscoveryFailure Refusal(string? code=null)=>new("DISCOVERY_HTTP_REFUSED",new(exact,(int)response.StatusCode,code));
                try
                {
                    if(response.Content.Headers.ContentLength>cap){if(refused)throw Refusal();throw new DiscoveryFailure("DISCOVERY_BODY_LIMIT");}
                    await using var stream=await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);int used=0,n;
                    while((n=await stream.ReadAsync(bytes.AsMemory(used,bytes.Length-used),ct).ConfigureAwait(false))>0)
                    {
                        used+=n;if(used>cap){if(refused)throw Refusal();throw new DiscoveryFailure("DISCOVERY_BODY_LIMIT");}
                    }
                    Check();
                    if(refused)throw Refusal(NumericErrorCode(bytes.AsMemory(0,used)));
                    try{return OkxObservationJson.Parse<T>(bytes.AsMemory(0,used),endpoint);}
                    catch(OkxObservationJson.VenueException e){throw new DiscoveryFailure("DISCOVERY_API_"+e.Code,new(exact,(int)response.StatusCode,e.Code));}
                }
                catch(IOException) when(refused){Check();throw Refusal();}
                catch(HttpRequestException) when(refused){Check();throw Refusal();}
                finally{CryptographicOperations.ZeroMemory(bytes);}
            }
            var timeStart=DateTimeOffset.UtcNow;var time=await Get<OkxTimeDto>("/api/v5/public/time",authenticated:false).ConfigureAwait(false);var receive=DateTimeOffset.UtcNow;
            if(time.Count!=1||time[0].Ts.Utc is not {} server||receive-timeStart>TimeSpan.FromSeconds(2)||(server-(timeStart+TimeSpan.FromTicks((receive-timeStart).Ticks/2))).Duration()>TimeSpan.FromSeconds(5))return new(null,"DISCOVERY_CLOCK_REFUSED");
            var accounts=await Get<OkxExecutionAccountDto>("/api/v5/account/config").ConfigureAwait(false);var a=accounts.Single();
            if(!Grammar.Match(a.Uid,"[0-9]{1,32}")||!Grammar.Match(a.MainUid,"[0-9]{1,32}")||a.Uid!=a.MainUid||expectedUid!=null&&(a.Uid!=expectedUid||a.MainUid!=expectedMainUid))return new(null,"DISCOVERY_IDENTITY_MISMATCH");
            if(a.Permissions is not ("read_only" or "read_only,trade" or "trade,read_only")||a.AccountLevel!="1"||a.AutoLoan||a.EnableSpotBorrow||a.SpotBorrowAutoRepay)return new(null,"DISCOVERY_UNSAFE_ACCOUNT");
            var balances=(await Get<OkxExecutionBalanceDto>("/api/v5/account/balance").ConfigureAwait(false)).Single().Details;
            if(balances.Any(b=>!Grammar.Match(b.Currency,"[A-Z0-9]{2,12}"))||balances.Select(b=>b.Currency).Distinct(StringComparer.Ordinal).Count()!=balances.Length) return new(null,"DISCOVERY_BALANCE_INVALID");
            var instruments=await Get<OkxExecutionInstrumentDto>("/api/v5/account/instruments","?instType=SPOT").ConfigureAwait(false);
            if(instruments.Any(i=>i.InstrumentType!="SPOT"||!Grammar.Instrument(i.Instrument))||instruments.Select(i=>i.Instrument).Distinct(StringComparer.Ordinal).Count()!=instruments.Count)return new(null,"DISCOVERY_INSTRUMENT_INVALID");
            Check();return new(new(a.Uid,a.MainUid,"Demo",provenance,Convert.ToHexString(SHA256.HashData(k)).ToLowerInvariant(),DateTimeOffset.UtcNow,balances,instruments.ToImmutableArray()),"CONNECTED_READ_ONLY_STOP");
        }
        catch(DiscoveryFailure e){return new(null,e.Code+(e.Diagnostic?.Suffix??""),e.Diagnostic);}
        catch(OperationCanceledException){return new(null,caller.IsCancellationRequested?"DISCOVERY_CANCELLED":"DISCOVERY_TIMEOUT_OR_UNSAFE_DIAGNOSTICS");}
        catch(OkxObservationJson.VenueException e){return new(null,"DISCOVERY_API_"+e.Code);}
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException or DecoderFallbackException or OkxObservationJson.CardinalityException){return new(null,"DISCOVERY_INVALID_PAYLOAD");}
        catch(Exception e) when(e is HttpRequestException or IOException){return new(null,"DISCOVERY_TRANSPORT_REFUSED");}
        finally
        {
            if(k!=null)CryptographicOperations.ZeroMemory(k);if(s!=null)CryptographicOperations.ZeroMemory(s);if(p!=null)CryptographicOperations.ZeroMemory(p);
            if(held){await Task.Delay(TimeSpan.FromMilliseconds(610)).ConfigureAwait(false);BootstrapInterlock.Release();}
        }
    }
    // Error envelopes may be gateway HTML or have no data array. Inspect only
    // one exact top-level code; malformed/ambiguous/non-numeric codes stay unknown.
    private static string? NumericErrorCode(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var doc=JsonDocument.Parse(bytes);
            if(doc.RootElement.ValueKind!=JsonValueKind.Object)return null;
            string? code=null;int count=0;
            foreach(var property in doc.RootElement.EnumerateObject())
            {
                if(!property.Name.Equals("code",StringComparison.OrdinalIgnoreCase))continue;
                if(++count!=1||property.Name!="code"||property.Value.ValueKind!=JsonValueKind.String)return null;
                code=property.Value.GetString();
            }
            return count==1&&(code=="0"||Grammar.Match(code,"[1-9][0-9]{4,7}"))?code:null;
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException){return null;}
    }
    private static bool Header(ReadOnlySpan<byte> b){if(b.Length is <=0 or >4096)return false;foreach(byte v in b)if(v is <33 or >126)return false;return true;}
    private sealed class DiscoveryFailure(string code,OkxEnrollmentDiagnostic? diagnostic=null):Exception
    {internal string Code=>code;internal OkxEnrollmentDiagnostic? Diagnostic=>diagnostic;}
}
