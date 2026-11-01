using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Xunit;
namespace Apex.Tests;
public sealed class OkxNativeControlTests
{
    [Fact,Trait("Gate","NativeWindows")]
    public async Task Production_provisioning_opens_native_only_form_and_cancel_preserves_config()
    {
        if(!OperatingSystem.IsWindows())return;
        await using var h=new NavalProductionHost();await h.Start();
        string before=System.IO.File.ReadAllText(System.IO.Path.Combine(h.DirectoryPath,"apex.config.json"));
        using var req=h.Request("/api/naval/okx/configure","operator","POST");
        req.Headers.Add("X-Apex-Local","1"); req.Content=new StringContent("{\"commandId\":\"native-cancel\"}",Encoding.UTF8,"application/json");
        var send=h.Client.SendAsync(req);bool seen=false;var deadline=DateTime.UtcNow.AddSeconds(8);
        while(!send.IsCompleted&&DateTime.UtcNow<deadline)
        {
            EnumWindows((w,_)=>{GetWindowThreadProcessId(w,out var pid);var title=new StringBuilder(256);GetWindowText(w,title,256);
                if(pid==h.Process!.Id&&title.ToString()=="APEX — konfiguracja OKX"){seen=true;PostMessage(w,0x111,new IntPtr(2),IntPtr.Zero);}return true;},IntPtr.Zero);
            await Task.Delay(50);
        }
        Assert.True(seen,"Production action must open the native form; an unavailable provider is not delivery.");
        using var response=await send;Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        Assert.Contains("NATIVE_CANCELLED",await response.Content.ReadAsStringAsync());
        Assert.Equal(before,System.IO.File.ReadAllText(System.IO.Path.Combine(h.DirectoryPath,"apex.config.json")));
    }
    private delegate bool Callback(IntPtr w,IntPtr p);
    [DllImport("user32.dll")]private static extern bool EnumWindows(Callback callback,IntPtr p);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr w,out int pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr w,StringBuilder text,int count);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr w,uint msg,IntPtr a,IntPtr b);
}
