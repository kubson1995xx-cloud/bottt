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
internal sealed record OkxNativeForm(byte[][]? Values,string Code);
// Native local-only inputs. No clipboard, browser, child-process arguments or plaintext files.
internal static class WindowsOkxDialog
{
    internal static async Task<byte[][]?> ShowAsync(string title,string explanation,IReadOnlyList<OkxNativeField> fields,CancellationToken ct,Func<byte[][],string?>? validate=null)=>
        (await ShowDetailedAsync(title,explanation,fields,ct,validate).ConfigureAwait(false)).Values;
    internal static Task<OkxNativeForm> ShowDetailedAsync(string title,string explanation,IReadOnlyList<OkxNativeField> fields,CancellationToken ct,Func<byte[][],string?>? validate=null,bool connect=false,Func<byte[][],CancellationToken,Task<string?>>? submit=null)
    {
        if(ct.IsCancellationRequested)return Task.FromResult(new OkxNativeForm(null,"CANCELLED"));
        if(!OperatingSystem.IsWindows()||!Environment.UserInteractive)return Task.FromResult(new OkxNativeForm(null,"INTERACTIVE_WINDOWS_REQUIRED"));
        var completion=new TaskCompletionSource<OkxNativeForm>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{completion.SetResult(Show(title,explanation,fields,ct,validate,connect,submit));}catch(Exception e) when(e is not OutOfMemoryException){completion.SetResult(new(null,"NATIVE_EXCEPTION"));}});
        try{thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();}
        catch(Exception e) when(e is not OutOfMemoryException){completion.TrySetResult(new(null,"THREAD_UNAVAILABLE"));}
        return completion.Task;
    }
    private static OkxNativeForm Show(string title,string explanation,IReadOnlyList<OkxNativeField> fields,CancellationToken ct,Func<byte[][],string?>? validate,bool connect,Func<byte[][],CancellationToken,Task<string?>>? submit)
    {
        if(!OperatingSystem.IsWindows())return new(null,"INTERACTIVE_WINDOWS_REQUIRED");
        if(fields.Count>12)throw new ArgumentException("NATIVE_FIELD_LIMIT");
        using var memory=new MemoryStream();using var w=new BinaryWriter(memory,Encoding.Unicode,true);
        // Long operator text (e.g. the Polish local-owner confirmation) must be fully visible, not clipped:
        // grow the explanation by an estimate of wrapped lines (~70 chars per 334-DLU line, ~8 DLU per line).
        int lines=System.Linq.Enumerable.Sum(explanation.Split((char)10),p=>Math.Max(1,(p.Length+69)/70));int explanationHeight=Math.Clamp(lines*8+4,38,240);int extra=explanationHeight-38;
        int offset=(connect?26:0)+extra;int height=100+offset+fields.Count*28;
        w.Write(0x90C808C4u);w.Write(0u);w.Write((ushort)(4+fields.Count*2+(connect?2:0)));w.Write((short)0);w.Write((short)0);w.Write((short)350);w.Write((short)height);
        w.Write((ushort)0);w.Write((ushort)0);Text(w,title);w.Write((ushort)9);Text(w,"Segoe UI");
        Item(w,0x50000000,8,6,334,explanationHeight,90,0x82,explanation);
        if(connect){Item(w,0x50000000,8,46+extra,150,18,80,0x82,"DEMO — tylko odczyt");Item(w,0x58000000,170,46+extra,170,18,81,0x80,"LIVE — zablokowane");}
        for(int i=0;i<fields.Count;i++)
        {
            Item(w,0x50000000,8,offset+46+i*28,334,10,200+i,0x82,fields[i].Label);
            Item(w,0x50810080u|(fields[i].Secret?0x20u:0),8,offset+57+i*28,334,14,100+i,0x81,fields[i].Initial);
        }
        Item(w,0x50000000,8,height-48,334,22,91,0x82,"");
        Item(w,0x50010001,176,height-22,78,15,1,0x80,connect?"Połącz":"Zatwierdź");Item(w,0x50010000,262,height-22,78,15,2,0x80,"Anuluj");
        byte[] template=memory.ToArray();var pin=GCHandle.Alloc(template,GCHandleType.Pinned);byte[][]? result=null;
        long until=Environment.TickCount64+120000;
        using var operation=CancellationTokenSource.CreateLinkedTokenSource(ct);Task<string?>? sending=null;
        void ClearFields(IntPtr window){for(int i=0;i<fields.Count;i++)SetDlgItemTextW(window,100+i,"");}
        DialogProc callback=(window,message,a,b)=>
        {
            if(message==0x110){SetTimer(window,new IntPtr(1),100,IntPtr.Zero);ShowWindow(window,5);SetForegroundWindow(window);BringWindowToTop(window);SetFocus(GetDlgItem(window,100));return IntPtr.Zero;}
            if(message==0x113&&sending?.IsCompleted==true){string? error=sending.IsCompletedSuccessfully?sending.Result:"CONNECT_LOCAL_REFUSED";sending=null;if(error==null&&!operation.IsCancellationRequested){ClearFields(window);EndDialog(window,new IntPtr(1));return new IntPtr(1);}if(result!=null)foreach(var value in result)CryptographicOperations.ZeroMemory(value);result=null;EnableWindow(GetDlgItem(window,1),true);SetDlgItemTextW(window,91,error??"CONNECT_CANCELLED");SetFocus(GetDlgItem(window,100));}
            if(message==0x113&&(ct.IsCancellationRequested||Environment.TickCount64>=until)){operation.Cancel();ClearFields(window);EndDialog(window,new IntPtr(2));return new IntPtr(1);}
            if(message==0x10){operation.Cancel();ClearFields(window);EndDialog(window,new IntPtr(2));return new IntPtr(1);}
            if(message==0x111&&((a.ToInt64()&0xffff)==1||(a.ToInt64()&0xffff)==2))
            {
                bool accept=(a.ToInt64()&0xffff)==1&&!ct.IsCancellationRequested;
                if(accept&&sending!=null)return new IntPtr(1);
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
                        string? error=validate?.Invoke(result);
                        if(error!=null)throw new InvalidDataException(error);
                    }
                    catch(Exception e) when(e is not OutOfMemoryException){foreach(var bytes in result)if(bytes!=null)CryptographicOperations.ZeroMemory(bytes);result=null;SetDlgItemTextW(window,91,e is InvalidDataException&&e.Message=="OKX_METADATA_INVALID"?"Niepoprawny lub istniejący zakres. DEMO/LIVE, unikalna nazwa, UID i waluta są wymagane.":"Uzupełnij wszystkie pola (1–4095 znaków). Formularz pozostaje otwarty.");SetFocus(GetDlgItem(window,100));return new IntPtr(1);}
                }
                if(accept&&result!=null&&submit!=null){var values=result;ClearFields(window);EnableWindow(GetDlgItem(window,1),false);SetDlgItemTextW(window,91,"Łączenie DEMO: uwierzytelniony odczyt, następnie chroniona zgoda Windows. STOP / Anuluj przerywa.");sending=Task.Run(()=>submit(values,operation.Token));return new IntPtr(1);}
                if(!accept)operation.Cancel();
                for(int i=0;i<fields.Count;i++)SetDlgItemTextW(window,100+i,"");
                EndDialog(window,new IntPtr(accept&&result!=null?1:2));return new IntPtr(1);
            }
            return IntPtr.Zero;
        };
        try{var code=DialogBoxIndirectParamW(GetModuleHandleW(null),pin.AddrOfPinnedObject(),IntPtr.Zero,callback,IntPtr.Zero);int native=code==new IntPtr(-1)?Marshal.GetLastWin32Error():0;if(code!=new IntPtr(1)||ct.IsCancellationRequested){operation.Cancel();if(sending!=null){try{sending.GetAwaiter().GetResult();}catch(Exception e) when(e is not OutOfMemoryException){}sending=null;}if(result!=null)foreach(var bytes in result)CryptographicOperations.ZeroMemory(bytes);return new(null,code==new IntPtr(-1)?"OS_ERROR_WIN32_"+native.ToString(System.Globalization.CultureInfo.InvariantCulture):ct.IsCancellationRequested?"CANCELLED":Environment.TickCount64>=until?"TIMEOUT":"USER_CANCELLED");}return new(result,"ACCEPTED");}
        finally{operation.Cancel();if(sending!=null){try{sending.GetAwaiter().GetResult();}catch(Exception e) when(e is not OutOfMemoryException){}if(result!=null)foreach(var value in result)CryptographicOperations.ZeroMemory(value);}pin.Free();GC.KeepAlive(callback);}
    }
    private static void Text(BinaryWriter w,string value){foreach(char c in value)w.Write((ushort)c);w.Write((ushort)0);}
    private static void Item(BinaryWriter w,uint style,int x,int y,int width,int height,int id,ushort cls,string title)
    {
        while(w.BaseStream.Position%4!=0)w.Write((byte)0);w.Write(style);w.Write(0u);w.Write((short)x);w.Write((short)y);w.Write((short)width);w.Write((short)height);w.Write((ushort)id);w.Write((ushort)0xffff);w.Write(cls);Text(w,title);w.Write((ushort)0);
    }
    private delegate IntPtr DialogProc(IntPtr w,uint msg,IntPtr a,IntPtr b);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]private static extern IntPtr DialogBoxIndirectParamW(IntPtr module,IntPtr template,IntPtr parent,DialogProc callback,IntPtr parameter);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern int GetDlgItemTextW(IntPtr w,int id,[Out]char[] text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)]private static extern bool SetDlgItemTextW(IntPtr w,int id,string text);
    [DllImport("user32.dll")]private static extern bool EndDialog(IntPtr w,IntPtr result);
    [DllImport("user32.dll")]private static extern IntPtr SetTimer(IntPtr w,IntPtr id,uint milliseconds,IntPtr callback);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr w,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr w);
    [DllImport("user32.dll")]private static extern bool BringWindowToTop(IntPtr w);
    [DllImport("user32.dll")]private static extern IntPtr SetFocus(IntPtr w);
    [DllImport("user32.dll")]private static extern IntPtr GetDlgItem(IntPtr w,int id);
    [DllImport("user32.dll")]private static extern bool EnableWindow(IntPtr w,bool enable);

}
