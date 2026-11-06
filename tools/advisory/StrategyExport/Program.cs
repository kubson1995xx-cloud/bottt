using System.Net;
using Apex.Core.Naval;
using Apex.Desktop.Okx;
if(args[0]=="--guard")
{
 if(!OperatingSystem.IsWindows())throw new Exception("native Windows required");
 string path=Path.GetFullPath(args[1]);
 if(!Path.IsPathFullyQualified(args[1]))throw new Exception("absolute store required");
 var leases=new List<Apex.Desktop.Services.WindowsPathSecurity.NativeObject>();
 try
 {
  var parent=new DirectoryInfo(Path.GetDirectoryName(path)!);
  var ancestors=new List<DirectoryInfo>();for(var p=parent;p!=null;p=p.Parent)ancestors.Add(p);ancestors.Reverse();
  foreach(var p in ancestors){var h=Apex.Desktop.Services.WindowsPathSecurity.Open(p.FullName);Apex.Desktop.Services.WindowsPathSecurity.Validate(h,false);leases.Add(h);}
  if(!Directory.Exists(path))Apex.Desktop.Services.WindowsPathSecurity.CreatePrivateDirectory(path);
  var root=Apex.Desktop.Services.WindowsPathSecurity.Open(path);Apex.Desktop.Services.WindowsPathSecurity.Validate(root,true);leases.Add(root);
  Console.WriteLine("ADVISORY_STORE_ADMITTED");Console.Out.Flush();Console.ReadLine();
 }
 finally{foreach(var lease in leases)lease.Dispose();}
 return;
}
var cutoff=DateTimeOffset.Parse("2026-11-06T00:00:00Z");
using var feed=new OkxPublicMarketFeed(new FixedClock(cutoff.AddHours(12)),new PublicResponse(File.ReadAllText(args[0])));
var series=await feed.ReadSeriesAsync(new NavalInstrument("BTC-EUR","BTC","EUR",1m,1m,1m),CancellationToken.None);
File.WriteAllBytes(args[1],OkxAdvisoryCanonicalData.Serialize(series,cutoff));
Console.WriteLine($"REAL_READSERIES_EXPORT {series.Bars.Count}");
sealed class FixedClock(DateTimeOffset now):TimeProvider { public override DateTimeOffset GetUtcNow()=>now; }
sealed class PublicResponse(string json):HttpMessageHandler
{
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
 {
  if(request.RequestUri!.AbsoluteUri!="https://eea.okx.com/api/v5/market/candles?bar=1Dutc&instId=BTC-EUR&limit=300")throw new Exception("wrong source");
  return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(json)});
 }
}
// Unused quote diagnostic projection only; candle and series types are actual Core.
namespace Apex.Desktop.Services { internal sealed class WindowsDataException(string reason):Exception(reason); }
namespace Apex.Desktop.Okx { internal sealed record OkxFiniteUnavailableBook(string Instrument,string Reason,int AskRows,int BidRows,DateTimeOffset SourceUtc); }
