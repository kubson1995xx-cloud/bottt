using System;
using System.Threading.Tasks;
namespace Apex.Tests;

// Isolated friend-only synthetic entry. Never dispatch production Main or accept a financial root.
internal static class PanelConnectEntry
{
    public static async Task<int> Main(string[] args)
    {
        if(args.Length!=2||args[0]!="panel-connect")return 90;
        OkxConditionalTradingTests.PanelCapture=string.IsNullOrEmpty(args[1])?null:args[1];
        try{await new OkxConditionalTradingTests(new ConsoleLog()).PanelScenario();return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
    private sealed class ConsoleLog:Xunit.Abstractions.ITestOutputHelper
    {
        public void WriteLine(string message)=>Console.WriteLine(message);
        public void WriteLine(string format,params object[] args)=>Console.WriteLine(format,args);
    }
}
