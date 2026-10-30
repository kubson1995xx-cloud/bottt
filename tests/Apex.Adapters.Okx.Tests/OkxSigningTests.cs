using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Apex.Adapters.Okx;
using Xunit;
namespace Apex.Adapters.Okx.Tests;
public class OkxSigningTests
{
    [Fact]
    public void ExactEscapedGetVector()
    {
        var secret = Encoding.UTF8.GetBytes("public-vector-secret"); const string stamp = "2026-10-30T12:00:00.123Z", path = "/api/v5/trade/order?instId=A%26B%3F%C5%BC&ordId=999999999999999999999";
        var expected = Convert.ToBase64String(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(stamp + "GET" + path)));
        var old = CultureInfo.CurrentCulture; try { CultureInfo.CurrentCulture = new("pl-PL"); Assert.Equal(expected, OkxGetSigner.Sign(secret, stamp, path)); foreach (var altered in new[] { path + "0", path.Replace("%26", "%2526"), path.Replace("instId=A%26B%3F%C5%BC&ordId=999999999999999999999", "ordId=999999999999999999999&instId=A%26B%3F%C5%BC") }) Assert.NotEqual(expected, OkxGetSigner.Sign(secret, stamp, altered)); Assert.NotEqual(expected, OkxGetSigner.Sign(secret, stamp.Replace("123", "124"), path)); } finally { CultureInfo.CurrentCulture = old; }
    }
    [Fact]
    public async Task UtcHeaderEqualsPreimage()
    {
        using var f = new OkxHttpFixture(); f.Bootstrap(); f.Add("/api/v5/trade/order?instId=A%26B%3F%C5%BC&ordId=opaque%3F%26", OkxHttpFixture.Envelope(OkxHttpFixture.Order()), System.Net.HttpStatusCode.ServiceUnavailable); f.Add("/api/v5/trade/order?instId=A%26B%3F%C5%BC&ordId=opaque%3F%26", OkxHttpFixture.Envelope(OkxHttpFixture.Order())); await using var c = f.Client(); var result = await c.GetOrderAsync(new("A&B?ż", "opaque?&"), default); Assert.Equal(OkxReadStatus.Observed, result.Status); Assert.Single(result.Rows); var req = f.Sent.Where(s => s.Path.Contains("/trade/order?")).ToArray(); Assert.Equal(2, req.Length); Assert.NotEqual(req[0].Timestamp, req[1].Timestamp); Assert.All(req, s => Assert.EndsWith("Z", s.Timestamp)); f.Done();
    }
}
