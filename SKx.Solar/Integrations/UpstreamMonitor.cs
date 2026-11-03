using Microsoft.Extensions.Options;
using SKx.Solar.Collector;

namespace SKx.Solar.Integrations;

/// <summary>Ein Erfolg genügt für den Pi, erst zwei Fehlschläge in Folge schalten auf Direktbetrieb um.</summary>
public sealed class UpstreamState
{
    private const int FailuresUntilDown = 2;
    private int _failures;
    private volatile bool _reachable;

    public bool IsReachable => _reachable;

    public void Report(bool success)
    {
        _failures = success ? 0 : _failures + 1;
        if (success) _reachable = true;
        else if (_failures >= FailuresUntilDown) _reachable = false;
    }

    /// <summary>Eine fehlgeschlagene Weiterleitung beweist den Ausfall sofort.</summary>
    public void MarkDown()
    {
        _failures = FailuresUntilDown;
        _reachable = false;
    }
}

/// <summary>
/// Beobachtet auf dem PC den Pi. Solange er aktuelle Daten hat, liefert er sie und der PC fragt den Logger nicht ab.
/// Antwortet er nicht oder hat selbst keine Daten, liest diese Instanz direkt vom Wechselrichter.
/// </summary>
public sealed class UpstreamMonitor(IOptions<SolarOptions> options, IHttpClientFactory http) : BackgroundService
{
    public const string ClientName = "upstream";
    private readonly TaskCompletionSource _firstCheck = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly UpstreamState _state = new();

    public string? Url { get; } = options.Value.Upstream.Url?.TrimEnd('/');
    public bool Configured => !string.IsNullOrWhiteSpace(Url);
    public bool IsReachable => Configured && _state.IsReachable;
    public Task FirstCheck => _firstCheck.Task;

    public void MarkDown() => _state.MarkDown();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Configured)
        {
            _firstCheck.TrySetResult();
            return;
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        do
        {
            _state.Report(await CheckAsync(stoppingToken));
            _firstCheck.TrySetResult();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<bool> CheckAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            // 503 heißt: Der Pi läuft, hat aber keine aktuellen Messwerte. Dann ist der direkte Weg besser.
            using var response = await http.CreateClient(ClientName).GetAsync($"{Url}/health", timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

public static class UpstreamProxy
{
    private static readonly HashSet<string> SkippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Transfer-Encoding", "Keep-Alive", "Content-Length", "Content-Type",
    };

    /// <summary>Leitet /api an den Pi weiter. Liefert false, wenn er nicht antwortet und die lokale Instanz übernehmen soll.</summary>
    public static async Task<bool> TryForwardAsync(HttpContext context, UpstreamMonitor upstream, IHttpClientFactory http)
    {
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            upstream.Url + context.Request.Path + context.Request.QueryString);
        if (context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            request.Content = new StreamContent(context.Request.Body);
            if (context.Request.ContentType is { } contentType)
                request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }
        foreach (var header in context.Request.Headers.Where(h => !SkippedHeaders.Contains(h.Key)))
            request.Headers.TryAddWithoutValidation(header.Key, (IEnumerable<string?>)header.Value);

        using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        headersTimeout.CancelAfter(TimeSpan.FromSeconds(60));
        HttpResponseMessage response;
        try
        {
            response = await http.CreateClient(UpstreamMonitor.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !context.RequestAborted.IsCancellationRequested)
        {
            upstream.MarkDown();
            return false;
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers).Where(h => !h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)))
                context.Response.Headers[header.Key] = header.Value.ToArray();
            await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        return true;
    }
}
