using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Apex.Okx.DemoAcceptance;
using Xunit;
namespace Apex.Tests;

internal static class OkxOwnedExitInventoryNative
{
    internal static async Task<int> Run()
    {
        LinuxInput.Harden();
        string root=Path.Combine("/home/ubuntu/apex-deliveries/current-market-funding-synthetic","owned-diagnostic-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root,(UnixFileMode)448);
        var type=typeof(FiniteStopRecovery).GetNestedType("PrivateOrderInventory",BindingFlags.NonPublic)!;
        object Make(params string[] names)
        {
            var item=Activator.CreateInstance(type,true)!;type.GetField("Endpoint",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(item,"/api/v5/trade/order");type.GetField("Path",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(item,"$.data[].<unknown>");
            foreach(string name in names)type.GetMethod("Observe",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(item,new object?[]{name,"String",1});return item;
        }
        using var credentials=new EphemeralCredentials(Encoding.ASCII.GetBytes("SensitiveApiCredential"),Encoding.ASCII.GetBytes("SensitiveSigningSecret"),Encoding.ASCII.GetBytes("SensitivePassphrase"));
        Task<bool> Publish(object item,string dir,string[]? ids=null)=>(Task<bool>)type.GetMethod("Publish",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(item,new object?[]{dir,credentials,ids??Array.Empty<string>(),CancellationToken.None})!;
        var cases=new System.Collections.Generic.List<string>();
        foreach(string name in new[]{"SensitiveApiCredential","prefixSensitiveSigningSecretsuffix","prefixSensitivePassphrasesuffix","notAllowed1","unknown-name"})
        {string dir=Path.Combine(root,"withheld-"+Guid.NewGuid().ToString("N"));Assert.False(await Publish(Make(name),dir));Assert.False(Directory.Exists(dir));cases.Add("withheld-"+name);}
        string identified=Path.Combine(root,"identifier");Assert.False(await Publish(Make("prefixKnownAccountsuffix"),identified,new[]{"KnownAccount"}));Assert.False(Directory.Exists(identified));
        cases.Add("known-identifier-withheld");
        string capped=Path.Combine(root,"capped");Assert.False(await Publish(Make(Enumerable.Repeat("unexpected",33).ToArray()),capped));Assert.False(Directory.Exists(capped));
        cases.Add("group-cap-withheld");
        string good=Path.Combine(root,"positive");Assert.True(await Publish(Make("privateUnrecognized"),good));string report=Path.Combine(good,"order-protection-identifiers.json");byte[] original=File.ReadAllBytes(report);Assert.Contains("privateUnrecognized",Encoding.ASCII.GetString(original));Assert.False(credentials.ContainsSecret(original));
        cases.Add("positive-private-publication");
        await Assert.ThrowsAsync<IOException>(()=>Publish(Make("differentIdentifier"),good));Assert.Equal(original,File.ReadAllBytes(report));
        cases.Add("no-clobber-preserved");
        // Calibrate the leak oracle independently: a real loaded secret in the
        // complete output is detected, not merely a forbidden field-name scan.
        Assert.True(credentials.ContainsSecret(Encoding.ASCII.GetBytes("prefixSensitiveSigningSecretsuffix")));
        cases.Add("deliberate-secret-leak-calibration");
        File.WriteAllText(Path.Combine(root,"diagnostic-summary.json"),JsonSerializer.Serialize(new{scenario="inventory-controls",caseCount=cases.Count,cases,passed=true,fullSecretArraysChecked=true,knownIdentifiersChecked=true,noClobber=true}));
        Directory.CreateDirectory(Path.Combine(root,"order-protection-reports"),(UnixFileMode)448);Console.WriteLine("SYNTHETIC_INVENTORY_CONTROLS_PASS; root="+root);return 0;
    }
}
