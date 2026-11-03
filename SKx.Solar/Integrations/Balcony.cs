using System.Text.Json;
using Microsoft.Extensions.Options;
using SKx.Solar.Collector;
using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

public sealed record BalconyPoint(long Ts, double W, double TodayKwh, double TotalKwh);

public static class BalconyParser
{
    /// <summary>Antwort von /getOutputData eines APsystems EZ1: p = Leistung, e = heute, te = Gesamtzähler, je Eingang.</summary>
    public static BalconyPoint Parse(string json, long ts)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.GetProperty("message").GetString() != "SUCCESS")
            throw new InvalidDataException("Das Balkonkraftwerk meldet einen Fehler.");
        var data = doc.RootElement.GetProperty("data");
        double Sum(string name) => data.GetProperty(name + "1").GetDouble() + data.GetProperty(name + "2").GetDouble();
        return new(ts, Sum("p"), Sum("e"), Sum("te"));
    }
}

public sealed class BalconyState
{
    private const int MaxAgeSeconds = 180;
    private volatile BalconyPoint? _latest;
    private volatile IReadOnlyList<BalconyPoint> _pending = [];

    public BalconyPoint? Latest
    {
        get => _latest;
        set => _latest = value;
    }

    /// <summary>Noch nicht gespeicherte Messwerte, damit Diagramme trotz seltenem Schreiben aktuell bleiben.</summary>
    public IReadOnlyList<BalconyPoint> Pending
    {
        get => _pending;
        set => _pending = value;
    }

    /// <summary>Nachts schaltet sich das Gerät ab, ein alter Wert zählt dann als keine Erzeugung.</summary>
    public double W(long now) => _latest is { } point && now - point.Ts < MaxAgeSeconds ? point.W : 0;

    public IReadOnlyDictionary<long, double> PendingMinutes() =>
        _pending.GroupBy(p => p.Ts / 60).ToDictionary(g => g.Key * 60, g => g.Average(p => p.W));
}

/// <summary>Liest das Balkonkraftwerk nur aus. Es läuft am Deye vorbei, darum fehlt seine Erzeugung sonst in der Bilanz.</summary>
public sealed class BalconyService(
    IOptions<SolarOptions> options,
    SolarDb db,
    BalconyState state,
    UpstreamMonitor upstream,
    IHttpClientFactory http,
    IntegrationStatus status,
    SettingsSignal signal,
    ILogger<BalconyService> logger) : BackgroundService
{
    private const int PollSeconds = 30;
    private const int FlushSeconds = 60;
    private const int SdFlushSeconds = 300;
    private readonly List<BalconyPoint> _buffer = [];
    private long _lastFlush = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool _failing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await upstream.FirstCheck.WaitAsync(stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var changed = signal.Changed;
                var o = options.Value;
                // Solange der Pi Daten liefert, liest nur er.
                if (!string.IsNullOrWhiteSpace(o.Balcony.Host) && !upstream.IsReachable)
                    await PollAsync(o.Balcony, stoppingToken);
                FlushCompletedMinutes(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), o.SdCardMode ? SdFlushSeconds : FlushSeconds);
                state.Pending = _buffer.ToArray();
                await SettingsSignal.WaitAsync(changed, TimeSpan.FromSeconds(PollSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Flush(_buffer.ToList());
        }
    }

    private async Task PollAsync(BalconyOptions balcony, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var json = await http.CreateClient().GetStringAsync($"http://{balcony.Host}:{balcony.Port}/getOutputData", timeout.Token);
            var point = BalconyParser.Parse(json, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            state.Latest = point;
            _buffer.Add(point);
            status.Ok(IntegrationStatus.Balcony);
            _failing = false;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidDataException or KeyNotFoundException or InvalidOperationException
            or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Im Dunkeln ist das Gerät aus, das ist kein Fehler.
            if (DateTime.Now.Hour is >= 10 and < 16)
                status.Fail(IntegrationStatus.Balcony, "Tagsüber nicht erreichbar. In der App AP EasyPower den lokalen Modus prüfen.");
            else
                status.ClearError(IntegrationStatus.Balcony);
            if (!_failing)
                logger.LogInformation("Balkonkraftwerk nicht erreichbar: {Message}", ex.Message);
            _failing = true;
        }
    }

    private void FlushCompletedMinutes(long now, int everySeconds)
    {
        if (now - _lastFlush < everySeconds)
            return;
        var minute = now / 60;
        if (Flush(_buffer.Where(p => p.Ts / 60 < minute).ToList()))
        {
            _buffer.RemoveAll(p => p.Ts / 60 < minute);
            _lastFlush = now;
        }
    }

    private bool Flush(List<BalconyPoint> points)
    {
        try
        {
            db.StoreBalcony(points, TimeZoneInfo.Local);
            return true;
        }
        catch (Exception ex)
        {
            // Puffer bleibt erhalten, der nächste Durchlauf versucht es erneut.
            logger.LogError(ex, "Speichern des Balkonkraftwerks fehlgeschlagen.");
            return false;
        }
    }
}
