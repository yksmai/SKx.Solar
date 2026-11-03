using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SKx.Solar.Alerts;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

public sealed record MirrorLive(InverterSample? Sample, DeviceInfo? Device, string? LastError, BalconyPoint? Balcony);

public sealed class BalconyMinuteRow
{
    public long Ts { get; set; }
    public double W { get; set; }
}

public sealed class BalconyDayRow
{
    public string Date { get; set; } = "";
    public double StartKwh { get; set; }
    public double EndKwh { get; set; }
}

public sealed class ForecastDayRow
{
    public string Date { get; set; } = "";
    public double NormKwh { get; set; }
}

/// <summary>Was sich selten ändert. Der Pi schickt es nur nach einer Änderung.</summary>
public sealed class MirrorPlanning
{
    public PlanningSettings Settings { get; set; } = new();
    public List<AuthUser> Users { get; set; } = [];
    public ForecastSnapshot? Forecast { get; set; }
    public List<PriceHour>? Prices { get; set; }
    public List<ForecastDayRow> ForecastDays { get; set; } = [];
    public List<AlertRow> Alerts { get; set; } = [];
    public double? Pv1W { get; set; }
    public double? Pv2W { get; set; }
}

public sealed class MirrorPush
{
    public MirrorLive Live { get; set; } = new(null, null, null, null);
    public List<MinuteRow> Minutes { get; set; } = [];
    public List<BalconyMinuteRow> BalconyMinutes { get; set; } = [];
    public List<DayCounters> Days { get; set; } = [];
    public List<BalconyDayRow> BalconyDays { get; set; } = [];
    public MirrorPlanning? Planning { get; set; }
}

/// <summary>Der Stand des Spiegels. So holt der Pi jede Lücke nach, auch wenn die Datenbank dort neu angelegt wurde.</summary>
public sealed record MirrorAck(long MinuteTs, long BalconyTs, string DayDate, string BalconyDayDate, bool NeedsPlanning);

/// <summary>Stellt auf dem Pi zusammen, was der Spiegel noch nicht hat.</summary>
public sealed class MirrorSource(
    IOptions<SolarOptions> options,
    SolarDb db,
    LiveState live,
    BalconyState balcony,
    ForecastService forecast,
    PriceService prices)
{
    private const int BatchSize = 2000;
    // Noch nicht gespeicherte Minuten liegen schon auf dem Spiegel. Die endgültigen Werte folgen spätestens nach 5 Minuten.
    private const int OverlapSeconds = 600;
    private const int StringsEverySeconds = 3600;
    private string? _sentPlanning;
    private string? _nextPlanning;
    private long _stringsAt;
    private (double? Pv1W, double? Pv2W) _strings;

    /// <summary>Ohne Stand geht nur der Live-Wert hinaus, die Antwort sagt dann, was fehlt.</summary>
    public (MirrorPush Push, bool More) Next(MirrorAck? ack, long now)
    {
        var state = live.Current;
        var push = new MirrorPush { Live = new(state.Sample, state.Device, state.LastError, balcony.Latest) };
        if (ack is null)
            return (push, true);

        push.Minutes = db.MinutesSince(ack.MinuteTs - OverlapSeconds, BatchSize);
        push.BalconyMinutes = db.BalconyMinutesSince(ack.BalconyTs - OverlapSeconds, BatchSize);
        push.Days = db.DayCountersSince(ack.DayDate);
        push.BalconyDays = db.BalconyDaysSince(ack.BalconyDayDate);
        var more = push.Minutes.Count == BatchSize || push.BalconyMinutes.Count == BatchSize;
        // Erst nach dem Nachholen: Sie würden den Stand sonst über die noch offene Lücke hinausschieben.
        if (!more)
        {
            var stored = push.Minutes.Select(m => m.Ts).ToHashSet();
            push.Minutes.AddRange(Aggregation.ToMinutes(live.Pending, TimeZoneInfo.Local).Where(m => !stored.Contains(m.Ts)));
            push.BalconyMinutes.AddRange(balcony.PendingMinutes().Select(p => new BalconyMinuteRow { Ts = p.Key, W = p.Value }));
        }

        var planning = Planning(now);
        var json = JsonSerializer.Serialize(planning);
        if (ack.NeedsPlanning || json != _sentPlanning)
        {
            push.Planning = planning;
            _nextPlanning = json;
        }
        return (push, more);
    }

    public void Confirm(MirrorPush push)
    {
        if (push.Planning is not null)
            _sentPlanning = _nextPlanning;
    }

    private MirrorPlanning Planning(long now)
    {
        if (now - _stringsAt >= StringsEverySeconds)
        {
            _strings = db.StringAverages(now - 30 * 86400L);
            _stringsAt = now;
        }
        var o = options.Value;
        return new MirrorPlanning
        {
            Settings = ForMirror(PlanningSettings.From(o)),
            Users = o.Auth.Users,
            Forecast = forecast.Current,
            Prices = prices.Current?.ToList(),
            ForecastDays = db.ForecastDaysSince(DateOnly.FromDateTime(DateTime.Now).AddDays(-31).ToString("yyyy-MM-dd")),
            Alerts = db.RecentAlerts(200).ToList(),
            Pv1W = _strings.Pv1W,
            Pv2W = _strings.Pv2W,
        };
    }

    /// <summary>Der Spiegel braucht nur, was die Anzeige rechnet. Zugänge und Adressen im Heimnetz bleiben auf dem Pi.</summary>
    public static PlanningSettings ForMirror(PlanningSettings settings)
    {
        var masked = settings.Masked();
        masked.Alerts = new AlertOptions
        {
            BatteryLowPercent = masked.Alerts.BatteryLowPercent,
            InverterTempLimitC = masked.Alerts.InverterTempLimitC,
            MinNotifySeverity = masked.Alerts.MinNotifySeverity,
        };
        masked.Inverter = null;
        masked.Backup = null;
        masked.Mqtt = new MqttOptions();
        masked.Sync = new SyncOptions();
        masked.TelegramTokenSet = false;
        masked.MqttPasswordSet = false;
        masked.SyncTokenSet = false;
        masked.Balcony = new BalconyOptions { Host = string.IsNullOrWhiteSpace(settings.Balcony.Host) ? null : "pi", Port = settings.Balcony.Port };
        return masked;
    }
}

/// <summary>Schickt die Daten zum Spiegel. Fällt er aus, misst der Pi weiter und holt die Lücke später nach.</summary>
public sealed class SyncService(
    IOptions<SolarOptions> options,
    MirrorSource source,
    IHttpClientFactory http,
    IntegrationStatus status,
    SettingsSignal signal,
    ILogger<SyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        MirrorAck? ack = null;
        var failing = false;
        while (true)
        {
            var changed = signal.Changed;
            var sync = options.Value.Sync;
            var more = false;
            if (sync.Configured && string.IsNullOrWhiteSpace(options.Value.Upstream.Url))
            {
                try
                {
                    (var push, more) = source.Next(ack, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    ack = await SendAsync(sync, push, stoppingToken);
                    source.Confirm(push);
                    status.Ok(IntegrationStatus.Sync);
                    failing = false;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !stoppingToken.IsCancellationRequested)
                {
                    more = false;
                    ack = null;
                    status.Fail(IntegrationStatus.Sync, ex.Message);
                    if (!failing)
                        logger.LogWarning("Übertragung zum Spiegel fehlgeschlagen: {Message}", ex.Message);
                    failing = true;
                }
            }
            if (!more)
                await SettingsSignal.WaitAsync(changed, TimeSpan.FromSeconds(Math.Max(sync.IntervalSeconds, 5)), stoppingToken);
        }
    }

    private async Task<MirrorAck> SendAsync(SyncOptions sync, MirrorPush push, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, sync.Url!.TrimEnd('/') + "/api/mirror/push")
        {
            Content = JsonContent.Create(push),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", sync.Token);
        using var response = await http.CreateClient().SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Der Spiegel antwortet mit {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<MirrorAck>(cancellationToken)
            ?? throw new HttpRequestException("Der Spiegel hat keinen Stand gemeldet.");
    }
}

/// <summary>Nimmt auf dem Server die Daten an und füllt damit dieselben Quellen, aus denen sonst der Pi seine Anzeige speist.</summary>
public sealed class MirrorReceiver(
    IOptions<SolarOptions> options,
    SolarDb db,
    LiveState live,
    BalconyState balcony,
    ForecastService forecast,
    PriceService prices,
    IntegrationStatus status)
{
    private const string FileName = "mirror-state.json";
    private readonly Lock _gate = new();
    private volatile MirrorPlanning? _planning;
    private long _lastPushTs;

    public long LastPushTs => Interlocked.Read(ref _lastPushTs);
    public (double? Pv1W, double? Pv2W) Strings => (_planning?.Pv1W, _planning?.Pv2W);

    public static bool TokenMatches(string? authorization, string? expected)
    {
        const string prefix = "Bearer ";
        if (string.IsNullOrEmpty(expected) || authorization is null || !authorization.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..])),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    /// <summary>Der Spiegel zeigt nur an. Ändern lässt sich alles nur auf dem Pi.</summary>
    public static bool AllowsWrite(string path) =>
        path.Equals("/api/login", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/logout", StringComparison.OrdinalIgnoreCase)
        || path.Equals("/api/mirror/push", StringComparison.OrdinalIgnoreCase);

    public MirrorAck Apply(MirrorPush push, long now)
    {
        lock (_gate)
        {
            db.StoreMirror(push);
            ApplyLive(push.Live);
            if (push.Planning is { } planning)
                ApplyPlanning(planning);
            Interlocked.Exchange(ref _lastPushTs, now);
            status.Ok(IntegrationStatus.Mirror);
            Save(new SavedState(push.Live, _planning, now));
            return db.MirrorCursor(_planning is null);
        }
    }

    /// <summary>Nach einem Neustart bleibt die Anzeige vollständig, auch wenn der Pi gerade nichts schickt.</summary>
    public void Restore()
    {
        var path = StatePath();
        if (!File.Exists(path))
            return;
        try
        {
            if (JsonSerializer.Deserialize<SavedState>(File.ReadAllText(path)) is not { } saved)
                return;
            lock (_gate)
            {
                ApplyLive(saved.Live);
                if (saved.Planning is { } planning)
                    ApplyPlanning(planning);
                Interlocked.Exchange(ref _lastPushTs, saved.LastPushTs);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Der Pi schickt den Stand mit der nächsten Übertragung neu.
            Console.Error.WriteLine($"{path} wird ignoriert: {ex.Message}");
        }
    }

    private void ApplyLive(MirrorLive state)
    {
        live.Restore(new LiveState.Snapshot(state.Sample, state.Device, state.LastError, null));
        balcony.Latest = state.Balcony;
    }

    private void ApplyPlanning(MirrorPlanning planning)
    {
        if (planning.Settings.Validate() is { } problem)
            throw new ArgumentException(problem);
        planning.Settings.ApplyTo(options.Value);
        // Fehlen die Konten auf dem Pi, bleibt die Seite gesperrt, statt offen zu stehen.
        if (planning.Users.Count > 0)
            options.Value.Auth.Users = planning.Users;
        forecast.Restore(planning.Forecast);
        prices.Restore(planning.Prices);
        _planning = planning;
    }

    private void Save(SavedState state)
    {
        var path = StatePath();
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        // Enthält die Passwort-Hashes.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    private string StatePath() => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.Path))!, FileName);

    private sealed record SavedState(MirrorLive Live, MirrorPlanning? Planning, long LastPushTs);
}
