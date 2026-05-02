using System.Diagnostics;

namespace PortMasterDesktop.Services;

/// <summary>
/// DelegatingHandler that records every outbound HTTP call through LogService.
/// Query strings (which may contain tokens/keys) are stripped before logging.
/// </summary>
public class LoggingHttpHandler : DelegatingHandler
{
    public LoggingHttpHandler(HttpMessageHandler? inner = null)
        : base(inner ?? new HttpClientHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var sw     = Stopwatch.StartNew();
        var method = request.Method.Method;
        var url    = request.RequestUri?.ToString() ?? "";

        try
        {
            var response = await base.SendAsync(request, ct);
            sw.Stop();
            var bytes = response.Content.Headers.ContentLength;
            LogService.Instance?.LogHttp(method, url, (int)response.StatusCode, sw.Elapsed, bytes);
            return response;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogService.Instance?.LogHttpError(method, url, ex, sw.Elapsed);
            throw;
        }
    }
}
