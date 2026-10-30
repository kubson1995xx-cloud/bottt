using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Apex.Desktop.Services;

[SupportedOSPlatform("windows")]
internal static class WindowsLocalAccessPrompt
{
    // Native modal only. Never clipboard, console, log, HTML or a browser URL.
    internal static bool Show(string login, string secret)
    {
        if (!Environment.UserInteractive) return false;
        try
        {
            string message = "Lokalny dostęp APEX Naval\n\nLogin: " + login + "\nHasło: " + secret +
                "\n\nZapisz hasło w swoim menedżerze haseł. Wprowadź je w natywnym oknie logowania przeglądarki (Basic)." +
                "\nNie są to dane brokera. Prezentacja jest syntetyczna i historyczna; nie oznacza gotowości do handlu." +
                "\n\nOK: kontynuuj. Anuluj: zakończ bez uruchamiania serwera. Dostęp można ponownie wyświetlić poleceniem --show-local-access.";
            // MB_OKCANCEL | MB_ICONINFORMATION | MB_DEFBUTTON2 | MB_SETFOREGROUND
            return MessageBoxW(IntPtr.Zero, message, "APEX — prywatny dostęp lokalny", 0x00010141) == 1;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or System.ComponentModel.Win32Exception) { return false; }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
