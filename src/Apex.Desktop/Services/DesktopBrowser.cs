using System;
using System.Diagnostics;

namespace Apex.Desktop.Services;

internal static class DesktopBrowser
{
    internal static Uri BuildUri(int port, bool navalEnabled)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new Uri($"http://127.0.0.1:{port}/" + (navalEnabled ? "?view=naval" : ""));
    }

    internal static bool TryOpen(Uri uri) => TryOpen(uri, null);

    // Only the external shell operation is replaceable, per source-test call.
    // No CLI, environment or configuration can select a launcher or URI authority.
    internal static bool TryOpen(Uri uri, Action<ProcessStartInfo>? launcher)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1" ||
            uri.Port is < 1 or > 65535 || uri.UserInfo != "" || uri.AbsolutePath != "/" ||
            (uri.Query != "" && uri.Query != "?view=naval") || uri.Fragment != "") return false;
        try
        {
            var request = new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true };
            if (launcher != null) launcher(request);
            else { using var process = Process.Start(request); }
            return true; // shell request only, not proof of navigation/rendering
        }
        catch (Exception) { return false; }
    }
}
