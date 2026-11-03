using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using SKx.Solar.Collector;
using SKx.Solar.Integrations;
using SKx.Solar.Storage;

namespace SKx.Solar.Alerts;

public sealed class AlertService(
    IOptions<SolarOptions> options,
    SolarDb db,
    LiveState live,
    IntegrationStatus status,
    IHttpClientFactory httpFactory,
    UpstreamMonitor upstream,
    BackupService backup,
    ILogger<AlertService> logger) : BackgroundService
{
    private readonly SolarOptions _options = options.Value;
    private readonly AlertTracker _tracker = new();
    private readonly long _processStart = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private long _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private (string Date, double? Median) _median = ("", null);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Im Pi-Betrieb überwacht und meldet der Pi. Die Karenzzeit läuft erst ab der Rückkehr in den Direktbetrieb.
            if (upstream.IsReachable)
            {
                _startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                continue;
            }
            try
            {
                Evaluate();
                await NotifyPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Prüfung der Meldungen fehlgeschlagen.");
            }
        }
    }

    private void Evaluate()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var localNow = DateTime.Now;
        var today = localNow.ToString("yyyy-MM-dd");
        var openRows = db.OpenAlerts();
        var open = openRows.Select(a => a.Key).ToHashSet();

        var input = new AlertInput(live.Current.Sample, now, _startedAt, localNow.Hour,
            _options.Alerts.BatteryLowPercent, _options.Alerts.InverterTempLimitC,
            MedianPv(today), db.Day(today)?.PvKwh, status.Get(IntegrationStatus.Backup)?.Error, ReadSystem(now));

        var (opened, closed) = _tracker.Step(AlertRules.Evaluate(input, open), open, now);
        foreach (var condition in opened)
            db.OpenAlert(new AlertRow { Key = condition.Key, Severity = condition.Severity, Title = condition.Title, Message = condition.Message, StartedTs = now });
        foreach (var row in openRows.Where(r => closed.Contains(r.Key)))
            db.CloseAlert(row.Id, now);
    }

    private SystemHealth ReadSystem(long now)
    {
        var pi = PiHealth.Read();
        var directory = Path.GetDirectoryName(Path.GetFullPath(db.Path))!;
        // Ohne eine Sicherung zählt die Laufzeit: Erst nach zwei Tagen ist das ein Befund.
        long? age = !_options.Backup.Enabled ? null : backup.LatestAgeSeconds(DateTime.Now) ?? now - _processStart;
        return new(pi.CpuTempC, pi.UnderVoltage, new DriveInfo(directory).AvailableFreeSpace / 1073741824.0, age);
    }

    private double? MedianPv(string today)
    {
        if (_median.Date == today)
            return _median.Median;
        var values = db.AllDays().Where(d => string.CompareOrdinal(d.Date, today) < 0).TakeLast(14).Select(d => d.PvKwh).Order().ToList();
        double? median = values.Count >= 7 ? values[values.Count / 2] : null;
        _median = (today, median);
        return median;
    }

    /// <summary>
    /// Versucht jeden eingerichteten Weg, ein Ausfall hält den anderen nicht auf.
    /// Liefert false, wenn nur ein Teil klappte. Scheitern alle, fliegt der letzte Fehler.
    /// </summary>
    public static async Task<bool> SendAsync(HttpClient http, AlertOptions a, string title, string message, string severity, CancellationToken ct)
    {
        var sent = 0;
        Exception? failure = null;
        async Task Try(string url, object body)
        {
            try
            {
                using var response = await http.PostAsJsonAsync(url, body, ct);
                response.EnsureSuccessStatusCode();
                sent++;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                failure = ex;
            }
        }

        if (!string.IsNullOrWhiteSpace(a.NtfyTopic))
            await Try(a.NtfyServer!.TrimEnd('/'), new
            {
                topic = a.NtfyTopic,
                title,
                message,
                priority = severity == Severity.Critical ? 5 : 3,
            });
        if (!string.IsNullOrWhiteSpace(a.TelegramBotToken) && !string.IsNullOrWhiteSpace(a.TelegramChatId))
            await Try($"https://api.telegram.org/bot{a.TelegramBotToken}/sendMessage", new
            {
                chat_id = a.TelegramChatId,
                text = $"{title}\n{message}",
            });

        if (failure is not null && sent == 0)
            throw failure;
        return failure is null;
    }

    private async Task NotifyPendingAsync(CancellationToken ct)
    {
        var a = _options.Alerts;
        var ntfy = !string.IsNullOrWhiteSpace(a.NtfyTopic);
        var telegram = !string.IsNullOrWhiteSpace(a.TelegramBotToken) && !string.IsNullOrWhiteSpace(a.TelegramChatId);
        if (!ntfy && !telegram)
            return;

        var pending = db.OpenAlerts().Where(r => r.NotifiedTs is null && Severity.Rank(r.Severity) >= Severity.Rank(a.MinNotifySeverity));
        foreach (var alert in pending)
        {
            try
            {
                var complete = await SendAsync(httpFactory.CreateClient(), a, alert.Title, alert.Message, alert.Severity, ct);
                // Ist sie auf einem Weg angekommen, gilt sie als zugestellt: Sonst käme sie dort alle 15 Sekunden erneut.
                db.MarkAlertNotified(alert.Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                if (complete)
                    status.Ok(IntegrationStatus.Notifications);
                else
                    status.Fail(IntegrationStatus.Notifications, "Einer der Benachrichtigungswege ist ausgefallen.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Bleibt offen und wird im nächsten Durchlauf erneut versucht. Die Meldung enthält nie das Token.
                status.Fail(IntegrationStatus.Notifications, $"Versand fehlgeschlagen ({ex.GetType().Name}).");
            }
        }
    }
}
