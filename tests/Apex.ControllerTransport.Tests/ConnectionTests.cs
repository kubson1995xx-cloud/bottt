using System.Reflection;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using Apex.ControllerAdmission.Tests;
using Apex.Desktop.Okx;
using Xunit;
namespace Apex.ControllerTransport.Tests;

public sealed class ConnectionTests
{
    internal static Type Store => typeof(OkxControllerSnapshot).Assembly.GetType("Apex.Desktop.Okx.WindowsOkxControllerConnectionStore") ?? throw new Xunit.Sdk.XunitException("MISSING_FIXED_CONNECTION_STORE");
    internal static object Call(string name, params object[] args)
    {
        try{return Store.GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,args)!;}
        catch(TargetInvocationException e){System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException!).Throw();throw;}
    }
    internal static Func<CancellationToken,Task<IOkxControllerFrameTransport>> Factory(string root)=>(Func<CancellationToken,Task<IOkxControllerFrameTransport>>)Call("CreateFactory",root);
    internal static string Json(WindowsOkxControllerTransportConfig c)=>JsonSerializer.Serialize(new {Schema="apex-controller-connection-v1",c.Host,c.Port,c.User,c.IdentitySha256,c.HostKey});
    internal static void Put(string root,string json)
    {
        string p=Path.Combine(root,"controller-connection.json");File.WriteAllText(p,json,new UTF8Encoding(false));
        var acl=new FileInfo(p).GetAccessControl();acl.SetOwner(System.Security.Principal.WindowsIdentity.GetCurrent().User!);new FileInfo(p).SetAccessControl(acl);
    }
    [Fact] public void Fixed_public_metadata_roundtrips_and_factory_is_passive_until_explicit_connect()
    {
        using var f=new NativeAdmissionTests.Profile();Put(f.Root,Json(f.Config));
        Assert.Equal(f.Config,Assert.IsType<WindowsOkxControllerTransportConfig>(Call("Load",f.Root)));
        var factory=Factory(f.Root);Assert.NotNull(factory);
        // Creating the delegate neither reads a profile nor creates its absent directory.
        var absent=f.Root+"-absent";Assert.NotNull(Factory(absent));Assert.False(Directory.Exists(absent));
    }
    [Theory][InlineData("duplicate")][InlineData("extra")][InlineData("array")][InlineData("string-port")][InlineData("schema")][InlineData("missing")][InlineData("identity")][InlineData("host")][InlineData("pin")][InlineData("oversize")]
    public void Closed_public_metadata_refuses_mutants_without_repair(string kind)
    {
        using var f=new NativeAdmissionTests.Profile();string good=Json(f.Config);Put(f.Root,good);Assert.Equal(f.Config,Call("Load",f.Root));
        string bad=kind switch {
            "duplicate"=>good.Insert(1,"\"Host\":\"vps.example\","),"extra"=>good.Insert(1,"\"Password\":\"not-accepted\","),"array"=>"["+good+"]",
            "string-port"=>good.Replace("2222","\"2222\""),"schema"=>good.Replace("apex-controller-connection-v1","other"),
            "missing"=>good.Replace("\"User\":\"apex\",",""),"identity"=>Json(f.Config with{IdentitySha256=new string('0',64)}),
            "host"=>Json(f.Config with{Host="other.example"}),"pin"=>Json(f.Config with{HostKey=new string('A',68)}),_=>new string('x',4097)};
        Put(f.Root,bad);Assert.ThrowsAny<Exception>(()=>Call("Load",f.Root));Assert.Equal(bad,File.ReadAllText(Path.Combine(f.Root,"controller-connection.json")));
    }
    [Fact] public async Task Missing_profile_cancelled_factory_and_wrong_platform_do_not_provision()
    {
        using var f=new NativeAdmissionTests.Profile();var factory=Factory(f.Root);
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>factory(cancel.Token));
        Assert.False(File.Exists(Path.Combine(f.Root,"controller-connection.json")));
        Assert.ThrowsAny<Exception>(()=>Call("Load",f.Root));
    }
}
