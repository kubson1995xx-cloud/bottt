using System;
using System.Runtime.InteropServices;
using System.Threading;
namespace Apex.Desktop.Okx;

// While the local scheduler is RUNNING: keep the system awake and warn before the
// console window is closed. Released on STOP, scheduler exit and process disposal.
// It never starts, stops or arms trading by itself.
internal sealed class WindowsLocalRunGuard : IDisposable
{
    internal const uint EsContinuous=0x80000000,EsSystemRequired=0x00000001;
    private readonly object sync=new();
    private readonly Thread? holder;
    private readonly AutoResetEvent changed=new(false);
    private bool desired,disposed;
    private volatile bool active;
    private uint lastApplied;
    internal bool Active=>active;
    internal uint LastAppliedFlags=>lastApplied;
    internal WindowsLocalRunGuard()
    {
        if(!OperatingSystem.IsWindows())return;
        holder=new Thread(Hold){IsBackground=true,Name="APEX keep-awake"};holder.Start();
    }
    internal void SetRunning(bool running)
    {
        lock(sync){if(disposed||desired==running)return;desired=running;}
        changed.Set();
        if(!OperatingSystem.IsWindows())return;
        try
        {
            if(running)
            {
                Console.Title="APEX — BOT DZIAŁA lokalnie — nie zamykaj tego okna";
                Console.Error.WriteLine("UWAGA: bot APEX DZIAŁA (RUNNING) na tym komputerze. Nie zamykaj tego okna i nie wyłączaj komputera. Aby zakończyć, naciśnij STOP w panelu, potem zamknij okno.");
            }
            else Console.Title="APEX — STOP";
        }
        catch(Exception e) when(e is System.IO.IOException or PlatformNotSupportedException or InvalidOperationException){}
        CloseButton(!running);
    }
    private void Hold()
    {
        bool applied=false;
        while(true)
        {
            changed.WaitOne();
            bool want,stop;lock(sync){want=desired&&!disposed;stop=disposed;}
            if(want!=applied)
            {
                uint flags=want?EsContinuous|EsSystemRequired:EsContinuous;
                if(SetThreadExecutionState(flags)!=0){applied=want;lastApplied=flags;active=want;}
            }
            if(stop){if(applied&&SetThreadExecutionState(EsContinuous)!=0){lastApplied=EsContinuous;}active=false;return;}
        }
    }
    // Classic console host only; Windows Terminal ignores the system menu, so the
    // Polish title/warning and the panel warning remain the primary signal there.
    private static void CloseButton(bool enabled)
    {
        try
        {
            IntPtr window=GetConsoleWindow();if(window==IntPtr.Zero)return;
            IntPtr menu=GetSystemMenu(window,false);if(menu==IntPtr.Zero)return;
            EnableMenuItem(menu,0xF060,enabled?0u:1u); // SC_CLOSE, MF_GRAYED
        }
        catch(Exception e) when(e is DllNotFoundException or EntryPointNotFoundException){}
    }
    public void Dispose()
    {
        lock(sync){if(disposed)return;disposed=true;desired=false;}
        changed.Set();holder?.Join(TimeSpan.FromSeconds(5));
        if(OperatingSystem.IsWindows())CloseButton(true);
    }
    [DllImport("kernel32.dll")]private static extern uint SetThreadExecutionState(uint flags);
    [DllImport("kernel32.dll")]private static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")]private static extern IntPtr GetSystemMenu(IntPtr window,bool revert);
    [DllImport("user32.dll")]private static extern int EnableMenuItem(IntPtr menu,uint item,uint flags);
}
