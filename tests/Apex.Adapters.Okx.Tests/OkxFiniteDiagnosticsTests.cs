using System.Net;
using System.Text.Json;
using Apex.Adapters.Okx.Execution;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public sealed class OkxFiniteDiagnosticsTests
{
    [Fact]
    public void General_submit_does_not_enable_post_only_for_ordinary_callers()
    {
        var scope=new OkxExecutionScope("Demo","123456","123456","USDC","key");
        Assert.Throws<ArgumentException>(()=>OkxPreparedRequest.Submit(scope,"BTC-USDC","buy","post_only","0.001","110","Client1",DateTimeOffset.FromUnixTimeMilliseconds(1793700000000),new("Algo1","112","108")));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized,"50110")]
    [InlineData(HttpStatusCode.BadRequest,"50101")]
    public async Task Actual_signed_write_retains_numeric_HTTP_and_OKX_errors_but_stays_Unknown(HttpStatusCode status,string code)
    {
        using var peer=new OkxExecutionHttpFixture();await using var client=peer.Client();
        peer.SubmitReply("{\"code\":\""+code+"\",\"msg\":\"SYNTHETIC_PRIVATE_MUST_NOT_PUBLISH\"}",status);
        var result=await client.SubmitAsync(peer.Request(),default);Assert.Equal(OkxWriteDisposition.Unknown,result.Disposition);
        var observed=Assert.Single(client.ReadDiagnostics());Assert.Equal((int)status,observed.HttpStatus);Assert.Equal(code,observed.Code);
        Assert.DoesNotContain("SYNTHETIC_PRIVATE",JsonSerializer.Serialize(observed));peer.Done();
    }
    [Theory]
    [InlineData("{\"code\":\"50110\",\"code\":\"50101\"}")]
    [InlineData("{\"Code\":\"50110\"}")]
    [InlineData("<html>private text</html>")]
    public async Task Error_envelope_ambiguity_is_numeric_unknown_not_a_rejection(string body)
    {
        using var peer=new OkxExecutionHttpFixture();await using var client=peer.Client();peer.SubmitReply(body,HttpStatusCode.Forbidden);
        Assert.Equal(OkxWriteDisposition.Unknown,(await client.SubmitAsync(peer.Request(),default)).Disposition);
        Assert.Null(Assert.Single(client.ReadDiagnostics()).Code);peer.Done();
    }
}
