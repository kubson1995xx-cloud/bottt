using System;
using System.IO;

namespace Apex.Desktop.Okx;

// Only public research inputs. No credentials, financial owner, execution port or network.
internal static class OkxAdvisoryReader
{
    internal const string ProductionRoot="/home/ubuntu/apex-tools/advisory-bridge/reports";
    internal static OkxAdvisoryStatus ReadProduction(DateTimeOffset now)=>ReadProductionForInstrument("SOL-EUR",now);
    internal static OkxAdvisoryStatus ReadProductionForInstrument(string instrument,DateTimeOffset now)=>ReadOwned(ProductionRoot,instrument,now);
    // Explicit isolated friend seam. Never substitutes the production root on failure.
    internal static bool TestRootAllowed(string root)
    {
        const string testParent="/home/ubuntu/apex-deliveries/okx-advisory-tests/okx-advisory-";
        return Path.IsPathFullyQualified(root)&&Path.GetFullPath(root)==root&&root.StartsWith(testParent,StringComparison.Ordinal)&&
            root!=ProductionRoot&&!root.StartsWith(ProductionRoot+"/",StringComparison.Ordinal);
    }
    internal static OkxAdvisoryStatus ReadForTests(string root,DateTimeOffset now)
        =>ReadInstrumentForTests(root,"SOL-EUR",now);
    internal static OkxAdvisoryStatus ReadInstrumentForTests(string root,string instrument,DateTimeOffset now)
        =>TestRootAllowed(root)?ReadOwned(root,instrument,now):OkxAdvisoryComparison.Unavailable("TEST_ROOT_REFUSED");
    private static byte[] Read(string root,string name)
    {
        using var handle=OkxLinuxPrivate.OpenFile(Path.Combine(root,name),OkxAdvisoryComparison.MaximumBytes);
        using var stream=new FileStream(handle,FileAccess.Read);
        // Bounded independently of a writer's size changes. Nonregular/symlink/multilink
        // files and unsafe ownership/modes are refused by the existing native reader.
        var buffer=new byte[OkxAdvisoryComparison.MaximumBytes+1];int count=0,read;
        while(count<buffer.Length&&(read=stream.Read(buffer,count,buffer.Length-count))!=0)count+=read;
        if(count>OkxAdvisoryComparison.MaximumBytes)throw new IOException("ADVISORY_SIZE_REFUSED");
        return buffer.AsSpan(0,count).ToArray();
    }
    private static OkxAdvisoryStatus ReadOwned(string root,string instrument,DateTimeOffset now)
    {
        if(!OkxAdvisoryComparison.InstrumentAllowed(instrument))return OkxAdvisoryComparison.Unavailable("INVALID_INSTRUMENT");
        try
        {
            // Only the exact approved literal can become a path component. No flat-root fallback.
            string directory=Path.Combine(root,instrument);
            byte[] vibe=Read(directory,"vibe-trading.json"),agents=Read(directory,"tradingagents.json");
            return OkxAdvisoryComparison.CompareForInstrument(vibe,agents,now,instrument);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or NotSupportedException)
        {return OkxAdvisoryComparison.Unavailable("REPORT_FILES_UNAVAILABLE") with{Instrument=instrument};}
    }
}
