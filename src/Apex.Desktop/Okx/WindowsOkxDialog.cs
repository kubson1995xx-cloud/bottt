using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Apex.Desktop.Okx;

internal sealed record OkxNativeField(string Label, bool Secret=false, string Initial="");
// Native local-only inputs. No clipboard, browser, child-process arguments or plaintext files.
internal static class WindowsOkxDialog
{
    internal static Task<byte[][]?> ShowAsync(string title,string explanation,IReadOnlyList<OkxNativeField> fields,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive||ct.IsCancellationRequested)return Task.FromResult<byte[][]?>(null);
        var completion=new TaskCompletionSource<byte[][]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(Show(title,explanation,fields,ct));}catch(Exception e) when(e is not OutOfMemoryException){completion.SetResult(null);}});
        thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();return completion.Task;
    }
    private static byte[][]? Show(string title,string explanation,IReadOnlyList<OkxNativeField> fields,CancellationToken ct)
    {
        if(!OperatingSystem.IsWindows())return null;
        if(fields.Count>12)throw new ArgumentException("NATIVE_FIELD_LIMIT");
        using var memory=new MemoryStream();using var w=new BinaryWriter(memory,Encoding.Unicode,true);
        int height=76+fields.Count*28;
        w.Write(0x90C808C4u);w.Write(0u);w.Write((ushort)(3+fields.Count*2));w.Write((short)0);w.Write((short)0);w.Write((short)350);w.Write((short)height);
        w.Write((ushort)0);w.Write((ushort)0);Text(w,title);w.Write((ushort)9);Text(w,"Segoe UI");
        Item(w,0x50000000,8,6,334,38,90,0x82,explanation);
        for(int i=0;i<fields.Count;i++)
        {
            Item(w,0x50000000,8,46+i*28,334,10,200+i,0x82,fields[i].Label);
            Item(w,0x50810080u|(fields[i].Secret?0x20u:0),8,57+i*28,334,14,100+i,0x81,fields[i].Initial);
        }
        Item(w,0x50010001,176,height-22,78,15,1,0x80,"Zatwierdź");Item(w,0x50010000,262,height-22,78,15,2,0x80,"Anuluj");
        byte[] template=memory.ToArray();var pin=GCHandle.Alloc(template,GCHandleType.Pinned);byte[][]? result=null;
        long until=Environment.TickCount64+120000;
        DialogProc callback=(window,message,a,b)=>
        {
            if(message==0x110){SetTimer(window,new IntPtr(1),100,IntPtr.Zero);return new IntPtr(1);}
            if(message==0x113&&(ct.IsCancellationRequested||Environment.TickCount64>=until)){EndDialog(window,new IntPtr(2));return new IntPtr(1);}
            if(message==0x10){EndDialog(window,new IntPtr(2));return new IntPtr(1);}
            if(message==0x111&&((a.ToInt64()&0xffff)==1||(a.ToInt64()&0xffff)==2))
            {
                bool accept=(a.ToInt64()&0xffff)==1&&!ct.IsCancellationRequested;
                if(accept)
                {
                    result=new byte[fields.Count][];
                    try
                    {
                        for(int i=0;i<fields.Count;i++)
                        {
                            var chars=new char[4097];
                            try{int count=GetDlgItemTextW(window,100+i,chars,chars.Length);if(count<=0||count>=4096)throw new InvalidDataException("NATIVE_INPUT_INVALID");result[i]=Encoding.UTF8.GetBytes(chars,0,count);}
                            finally{Array.Clear(chars);}
                        }
                    }
                    catch{foreach(var bytes in result)if(bytes!=null)CryptographicOperations.ZeroMemory(bytes);result=null;}
                }
                for(int i=0;i<fields.Count;i++)SetDlgItemTextW(window,100+i,"");
                EndDialog(window,new IntPtr(accept&&result!=null?1:2));return new IntPtr(1);
            }
            return IntPtr.Zero;
        };
        try{var code=DialogBoxIndirectParamW(GetModuleHandleW(null),pin.AddrOfPinnedObject(),IntPtr.Zero,callback,IntPtr.Zero);if(code!=new IntPtr(1)){if(result!=null)foreach(var bytes in result)CryptographicOperations.ZeroMemory(bytes);return null;}return result;}
        finally{pin.Free();GC.KeepAlive(callback);}
    }
    private static void Text(BinaryWriter w,string value){foreach(char c in value)w.Write((ushort)c);w.Write((ushort)0);}
    private static void Item(BinaryWriter w,uint style,int x,int y,int width,int height,int id,ushort cls,string title)
    {
        while(w.BaseStream.Position%4!=0)w.Write((byte)0);w.Write(style);w.Write(0u);w.Write((short)x);w.Write((short)y);w.Write((short)width);w.Write((short)height);w.Write((ushort)id);w.Write((ushort)0xffff);w.Write(cls);Text(w,title);w.Write((ushort)0);
    }
    private delegate IntPtr DialogProc(IntPtr w,uint msg,IntPtr a,IntPtr b);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern IntPtr DialogBoxIndirectParamW(IntPtr module,IntPtr template,IntPtr parent,DialogProc callback,IntPtr parameter);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int GetDlgItemTextW(IntPtr w,int id,[Out]char[] text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern bool SetDlgItemTextW(IntPtr w,int id,string text);
    [DllImport("user32.dll")]private static extern bool EndDialog(IntPtr w,IntPtr result);
    [DllImport("user32.dll")]private static extern IntPtr SetTimer(IntPtr w,IntPtr id,uint milliseconds,IntPtr callback);
}
