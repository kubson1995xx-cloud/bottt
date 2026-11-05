using System.Diagnostics;
using System.Text.Json;
namespace Apex.Windows.Tests;
// Adapter for the existing bounded native CDP driver; only public synthetic evidence.
internal static class WindowsTestHost
{
    internal static async Task Until(Func<Task<bool>> test,string failure,int seconds=35)
    {var s=Stopwatch.StartNew();while(s.Elapsed<TimeSpan.FromSeconds(seconds)){if(await test())return;await Task.Delay(50);}throw new Xunit.Sdk.XunitException(failure);}
    internal static void Evidence(string name,object value)
    {
        string? root=Environment.GetEnvironmentVariable("APEX_COMPOSITION_EVIDENCE");
        if(root==null)return;
        File.WriteAllText(Path.Combine(root,name+".json"),JsonSerializer.Serialize(value));
    }
}
