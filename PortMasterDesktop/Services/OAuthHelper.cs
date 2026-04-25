using System.Diagnostics;
using System.Net;
using System.Web;

namespace PortMasterDesktop.Services;

/// <summary>
/// Runs a local HTTP server to receive the OAuth callback, then opens the auth URL
/// in the user's default browser.  Works on Linux/Windows/macOS without any URL-scheme
/// registration.
/// </summary>
public static class OAuthHelper
{
    /// <summary>
    /// Picks a free local port, starts a listener, calls <paramref name="buildAuthUrl"/>
    /// with the resulting redirect URI, opens that URL in the default browser, and
    /// waits for the callback.
    ///
    /// Returns the query-string parameters from the redirect, or null on timeout/cancel.
    /// </summary>
    public static async Task<Dictionary<string, string>?> AuthenticateAsync(
        Func<string, string> buildAuthUrl,
        string callbackPath = "/callback",
        int timeoutSeconds = 120,
        CancellationToken ct = default)
    {
        var port = GetFreePort();
        var redirectUri = $"http://localhost:{port}{callbackPath}";

        var authUrl = buildAuthUrl(redirectUri);

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        OpenBrowser(authUrl);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            var contextTask = listener.GetContextAsync();
            await Task.WhenAny(contextTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (!contextTask.IsCompleted) return null;

            var context = await contextTask;
            var query = HttpUtility.ParseQueryString(context.Request.Url?.Query ?? "");

            var html = "<html><body><h2>Authentication complete.</h2><p>You can close this tab.</p>"
                     + "<script>window.close();</script></body></html>";
            var buf = System.Text.Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength64 = buf.Length;
            await context.Response.OutputStream.WriteAsync(buf, cts.Token);
            context.Response.Close();

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? key in query.AllKeys)
                if (key != null) result[key] = query[key] ?? "";
            return result;
        }
        catch (OperationCanceledException) { return null; }
        finally { listener.Stop(); }
    }

    public static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch
        {
            try { Process.Start("xdg-open", url); }
            catch { /* best effort */ }
        }
    }

    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
