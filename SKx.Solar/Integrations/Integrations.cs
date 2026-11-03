using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using MQTTnet;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

public sealed record IntegrationState(long? LastOkTs, string? Error, long? ErrorTs);

/// <summary>Letzter Stand der optionalen Dienste, für die Systemseite und die Meldungen.</summary>
public sealed class IntegrationStatus
{
    public const string Forecast = "forecast";
    public const string Prices = "prices";
    public const string Mqtt = "mqtt";
    public const string Backup = "backup";
    public const string Notifications = "notifications";
    public const string Balcony = "balcony";
    public const string Sync = "sync";
    public const string Mirror = "mirror";

    private readonly ConcurrentDictionary<string, IntegrationState> _states = new();

    public void Ok(string key) => _states[key] = new(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, null);

    public void Fail(string key, string error) => _states.AddOrUpdate(key,
        _ => new(null, error, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
        (_, old) => old with { Error = error, ErrorTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

    public void ClearError(string key) => _states.AddOrUpdate(key, _ => new(null, null, null), (_, old) => old with { Error = null, ErrorTs = null });

    public IntegrationState? Get(string key) => _states.GetValueOrDefault(key);
}

public sealed record ForecastHour(long T, double W);
public sealed record ForecastDay(string Date, double Kwh);
public sealed record ForecastSnapshot(long FetchedTs, IReadOnlyList<ForecastHour> Hours, IReadOnlyList<ForecastDay> Days);

public static class ForecastCalculator
{
    /// <summary>
    /// Addiert einen Dachbereich: Einstrahlung W/m² × kWp × Performance Ratio, bezogen auf 1000 W/m².
    /// Open-Meteo liefert das Mittel der vorherigen Stunde, hier zählt jeder Wert für den Beginn seiner Stunde.
    /// Der erste Wert gehört damit noch zum Vortag und entfällt.
    /// </summary>
    public static void AddPlane(SortedDictionary<long, double> hours, string json, double kwp, double performanceRatio)
    {
        using var doc = JsonDocument.Parse(json);
        var hourly = doc.RootElement.GetProperty("hourly");
        var times = hourly.GetProperty("time");
        var radiation = hourly.GetProperty("global_tilted_irradiance");
        for (var i = 1; i < times.GetArrayLength(); i++)
        {
            if (radiation[i].ValueKind != JsonValueKind.Number)
                continue;
            var t = times[i].GetInt64() - 3600;
            hours[t] = hours.GetValueOrDefault(t) + radiation[i].GetDouble() * kwp * performanceRatio;
        }
    }

    public static ForecastSnapshot Build(SortedDictionary<long, double> hours, long now, TimeZoneInfo zone)
    {
        var days = hours.GroupBy(h => Aggregation.LocalDate(h.Key, zone))
            .Select(g => new ForecastDay(g.Key, Math.Round(g.Sum(h => h.Value) / 1000, 1))).ToList();
        return new(now, hours.Select(h => new ForecastHour(h.Key, Math.Round(h.Value))).ToList(), days);
    }
}

public sealed class ForecastService(IOptions<SolarOptions> options, IHttpClientFactory http, IntegrationStatus status, SettingsSignal signal, SolarDb db) : BackgroundService
{
    private volatile ForecastSnapshot? _current;
    public ForecastSnapshot? Current => _current;
    public void Restore(ForecastSnapshot? snapshot) => _current = snapshot;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var changed = signal.Changed;
            var f = options.Value.Forecast;
            if (!f.Enabled)
            {
                _current = null;
                await SettingsSignal.WaitAsync(changed, TimeSpan.FromHours(1), stoppingToken);
                continue;
            }
            try
            {
                var hours = new SortedDictionary<long, double>();
                foreach (var plane in f.Planes)
                {
                    var url = string.Create(CultureInfo.InvariantCulture,
                        $"https://api.open-meteo.com/v1/forecast?latitude={f.Latitude}&longitude={f.Longitude}&hourly=global_tilted_irradiance&tilt={plane.TiltDegrees}&azimuth={plane.AzimuthDegrees}&forecast_days=3&timeformat=unixtime&timezone=auto");
                    ForecastCalculator.AddPlane(hours, await http.CreateClient().GetStringAsync(url, stoppingToken), plane.Kwp, f.PerformanceRatio);
                }
                _current = ForecastCalculator.Build(hours, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), TimeZoneInfo.Local);
                // Ohne Verlustfaktor speichern: So bleibt der Vergleich gültig, wenn sich der Faktor ändert.
                db.SaveForecast(DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    _current.Days.Select(d => (d.Date, d.Kwh / f.PerformanceRatio)));
                status.Ok(IntegrationStatus.Forecast);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                status.Fail(IntegrationStatus.Forecast, ex.Message);
            }
            await SettingsSignal.WaitAsync(changed, TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

public sealed record PriceHour(long T, double Ct);

public static class PriceCalculator
{
    /// <summary>Börsenpreis Eur/MWh in ct/kWh, plus Aufschlag und Umsatzsteuer aus den Einstellungen.</summary>
    public static IReadOnlyList<PriceHour> Parse(string json, double surchargeCt, double vatPercent)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new List<PriceHour>();
        foreach (var item in doc.RootElement.GetProperty("data").EnumerateArray())
        {
            var ct = (item.GetProperty("marketprice").GetDouble() / 10 + surchargeCt) * (1 + vatPercent / 100);
            result.Add(new(item.GetProperty("start_timestamp").GetInt64() / 1000, Math.Round(ct, 2)));
        }
        return result;
    }

    public static IReadOnlyList<long> CheapestHours(IReadOnlyList<PriceHour> hours, long now, int count) =>
        hours.Where(h => h.T + 3600 > now).OrderBy(h => h.Ct).Take(count).Select(h => h.T).ToList();
}

public sealed class PriceService(IOptions<SolarOptions> options, IHttpClientFactory http, IntegrationStatus status, SettingsSignal signal) : BackgroundService
{
    private volatile IReadOnlyList<PriceHour>? _current;
    public IReadOnlyList<PriceHour>? Current => _current;
    public void Restore(IReadOnlyList<PriceHour>? hours) => _current = hours;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var changed = signal.Changed;
            var p = options.Value.DynamicPrices;
            if (!p.Enabled)
            {
                _current = null;
                await SettingsSignal.WaitAsync(changed, TimeSpan.FromHours(1), stoppingToken);
                continue;
            }
            var host = p.Country.Equals("at", StringComparison.OrdinalIgnoreCase) ? "api.awattar.at" : "api.awattar.de";
            try
            {
                _current = PriceCalculator.Parse(await http.CreateClient().GetStringAsync($"https://{host}/v1/marketdata", stoppingToken), p.SurchargeCtPerKwh, p.VatPercent);
                status.Ok(IntegrationStatus.Prices);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                status.Fail(IntegrationStatus.Prices, ex.Message);
            }
            await SettingsSignal.WaitAsync(changed, TimeSpan.FromHours(1), stoppingToken);
        }
    }
}

public static class MqttPayload
{
    /// <summary>Überschuss = Einspeisung plus Batterieladung, also Leistung, die ein Verbraucher nutzen könnte.</summary>
    public static string Build(InverterSample s, double balconyW = 0) => JsonSerializer.Serialize(new
    {
        ts = s.Ts,
        pv_w = s.PvW,
        balcony_w = balconyW,
        load_w = s.LoadW,
        grid_w = s.GridW,
        battery_w = s.BatteryW,
        battery_soc = s.BatterySoc,
        export_w = Math.Max(-s.GridW, 0),
        surplus_w = Math.Max(-s.GridW, 0) + Math.Max(-s.BatteryW, 0),
        state = DeyeDecoder.StateName(s.State),
        total_pv_kwh = s.TotalPvKwh,
        total_load_kwh = s.TotalLoadKwh,
        total_import_kwh = s.TotalImportKwh,
        total_export_kwh = s.TotalExportKwh,
        total_charge_kwh = s.TotalChargeKwh,
        total_discharge_kwh = s.TotalDischargeKwh,
    });

    private static readonly (string Key, string Name, string? Unit, string? DeviceClass, string? StateClass)[] Sensors =
    [
        ("pv_w", "PV-Leistung", "W", "power", "measurement"),
        ("balcony_w", "Balkonkraftwerk", "W", "power", "measurement"),
        ("load_w", "Verbrauch", "W", "power", "measurement"),
        ("grid_w", "Netz", "W", "power", "measurement"),
        ("battery_w", "Batterieleistung", "W", "power", "measurement"),
        ("battery_soc", "Ladestand", "%", "battery", "measurement"),
        ("export_w", "Einspeisung", "W", "power", "measurement"),
        ("surplus_w", "Überschuss", "W", "power", "measurement"),
        ("state", "Status", null, null, null),
        ("total_pv_kwh", "PV gesamt", "kWh", "energy", "total_increasing"),
        ("total_load_kwh", "Verbrauch gesamt", "kWh", "energy", "total_increasing"),
        ("total_import_kwh", "Netzbezug gesamt", "kWh", "energy", "total_increasing"),
        ("total_export_kwh", "Einspeisung gesamt", "kWh", "energy", "total_increasing"),
        ("total_charge_kwh", "Batterie geladen gesamt", "kWh", "energy", "total_increasing"),
        ("total_discharge_kwh", "Batterie entladen gesamt", "kWh", "energy", "total_increasing"),
    ];

    public static string AvailabilityTopic(string topic) => topic + "/availability";

    /// <summary>Home-Assistant-Discovery: je Sensor ein Thema und seine Beschreibung.</summary>
    public static IEnumerable<(string Topic, string Payload)> Discovery(string stateTopic) => Sensors.Select(s => (
        $"homeassistant/sensor/skx_solar/{s.Key}/config",
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["name"] = s.Name,
            ["unique_id"] = $"skx_solar_{s.Key}",
            ["state_topic"] = stateTopic,
            ["value_template"] = $"{{{{ value_json.{s.Key} }}}}",
            ["availability_topic"] = AvailabilityTopic(stateTopic),
            ["unit_of_measurement"] = s.Unit,
            ["device_class"] = s.DeviceClass,
            ["state_class"] = s.StateClass,
            ["device"] = new { identifiers = new[] { "skx_solar" }, name = "SKx Solar", manufacturer = "Deye" },
        }.Where(p => p.Value is not null).ToDictionary())));
}

/// <summary>
/// Veröffentlicht die Werte für Wallbox, Heizstab oder Home Assistant. Die App abonniert nichts und
/// schaltet selbst nichts: Entscheidungen trifft der Empfänger, der Wechselrichter wird weiterhin nur gelesen.
/// </summary>
public sealed class MqttService(IOptions<SolarOptions> options, LiveState live, BalconyState balcony, IntegrationStatus status, SettingsSignal signal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (true)
        {
            var changed = signal.Changed;
            var m = options.Value.Mqtt;
            if (m.Enabled)
                await PublishUntilChangedAsync(m, changed, stoppingToken);
            else
                await SettingsSignal.WaitAsync(changed, TimeSpan.FromHours(1), stoppingToken);
        }
    }

    private async Task PublishUntilChangedAsync(MqttOptions m, Task changed, CancellationToken stoppingToken)
    {
        using var client = new MqttClientFactory().CreateMqttClient();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.PollSeconds));
        var availability = MqttPayload.AvailabilityTopic(m.Topic);
        long lastTs = 0;
        do
        {
            try
            {
                if (changed.IsCompleted)
                {
                    if (client.IsConnected)
                        await client.DisconnectAsync(cancellationToken: stoppingToken);
                    return;
                }
                if (!client.IsConnected)
                {
                    var builder = new MqttClientOptionsBuilder().WithTcpServer(m.Host, m.Port).WithClientId($"skx-solar-{Environment.MachineName}")
                        .WithWillTopic(availability).WithWillPayload("offline").WithWillRetain();
                    if (!string.IsNullOrEmpty(m.User))
                        builder.WithCredentials(m.User, m.Password);
                    await client.ConnectAsync(builder.Build(), stoppingToken);
                    if (m.HomeAssistant)
                        foreach (var (topic, payload) in MqttPayload.Discovery(m.Topic))
                            await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(topic).WithPayload(Encoding.UTF8.GetBytes(payload)).WithRetainFlag().Build(), stoppingToken);
                    await client.PublishAsync(new MqttApplicationMessageBuilder().WithTopic(availability).WithPayload("online"u8.ToArray()).WithRetainFlag().Build(), stoppingToken);
                }
                if (live.Current.Sample is { } sample && sample.Ts != lastTs)
                {
                    var message = new MqttApplicationMessageBuilder().WithTopic(m.Topic)
                        .WithPayload(Encoding.UTF8.GetBytes(MqttPayload.Build(sample, balcony.W(DateTimeOffset.UtcNow.ToUnixTimeSeconds())))).WithRetainFlag().Build();
                    await client.PublishAsync(message, stoppingToken);
                    lastTs = sample.Ts;
                    status.Ok(IntegrationStatus.Mqtt);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
            {
                status.Fail(IntegrationStatus.Mqtt, ex.Message);
                if (changed.IsCompleted)
                    return;
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

public sealed class BackupService(IOptions<SolarOptions> options, SolarDb db, IntegrationStatus status, SettingsSignal signal) : BackgroundService
{
    private const string Prefix = "solar-";
    private volatile string? _latestName;
    private volatile bool _listed;
    private DateOnly? _done;
    private string? _doneIn;
    private string? _seenIn;

    public string Directory => BackupDirectory(options.Value.Backup, db.Path);

    public static string BackupDirectory(BackupOptions backup, string dbPath) =>
        string.IsNullOrWhiteSpace(backup.Directory)
            ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "backups")
            : backup.Directory;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Ein hängendes NAS darf den Start der übrigen Dienste nicht aufhalten.
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            // Vor der Arbeit abholen, damit eine Änderung im Dashboard sofort greift.
            var changed = signal.Changed;
            RunDue(DateTime.Now);
            await SettingsSignal.WaitAsync(changed, TimeSpan.FromMinutes(10), stoppingToken);
        }
    }

    private void RunDue(DateTime now)
    {
        var b = options.Value.Backup;
        var directory = Directory;
        var today = DateOnly.FromDateTime(now);
        // Ein neuer Ordner zählt wie ein neuer Tag, die Anzeige liest ihn neu ein.
        if (directory != _seenIn)
        {
            _listed = _seenIn is null && _listed;
            _seenIn = directory;
        }
        // Ist der Tag erledigt, bleibt das NAS in Ruhe und darf sich trennen und schlafen legen.
        if (!b.Enabled || now.Hour < b.Hour || (_done == today && _doneIn == directory))
            return;
        try
        {
            var file = Path.Combine(directory, $"{Prefix}{now:yyyy-MM-dd}.db");
            if (!File.Exists(file))
                RunBackup(db, directory, file, b.KeepDays, now);
            SettingsStore.CopyTo(db.Path, directory);
            _latestName = Path.GetFileName(file);
            _listed = true;
            _done = today;
            _doneIn = directory;
            status.Ok(IntegrationStatus.Backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            status.Fail(IntegrationStatus.Backup, ex.Message);
        }
    }

    public static void RunBackup(SolarDb db, string directory, string file, int keepDays, DateTime now)
    {
        System.IO.Directory.CreateDirectory(directory);
        // Erst unter temporärem Namen schreiben: Eine halbe Datei zählt so nie als fertige Sicherung.
        var temp = file + ".tmp";
        if (File.Exists(temp)) File.Delete(temp);
        db.CreateSnapshot(temp);
        File.Move(temp, file, overwrite: true);
        Prune(directory, keepDays, now);
    }

    /// <summary>Löscht nur eigene Sicherungen anhand des Datums im Namen, andere Dateien bleiben unangetastet.</summary>
    public static void Prune(string directory, int keepDays, DateTime now)
    {
        var limit = DateOnly.FromDateTime(now).AddDays(-keepDays);
        foreach (var path in System.IO.Directory.GetFiles(directory, $"{Prefix}*.db"))
        {
            var name = Path.GetFileNameWithoutExtension(path)[Prefix.Length..];
            if (DateOnly.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < limit)
                File.Delete(path);
        }
    }

    /// <summary>Alter der neuesten Sicherung laut Dateinamen, null wenn es keine gibt. Liest das Ziel nur einmal, danach zählt der eigene Stand.</summary>
    public long? LatestAgeSeconds(DateTime now)
    {
        if (!_listed)
        {
            _latestName = List().LastOrDefault().Name;
            _listed = true;
        }
        var latest = _latestName;
        return latest is not null && DateOnly.TryParseExact(latest[Prefix.Length..^3], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? (long)(now - date.ToDateTime(TimeOnly.MinValue)).TotalSeconds
            : null;
    }

    public IReadOnlyList<(string Name, long Size)> List() =>
        System.IO.Directory.Exists(Directory)
            ? System.IO.Directory.GetFiles(Directory, $"{Prefix}*.db").Order().Select(f => (Path.GetFileName(f), new FileInfo(f).Length)).ToList()
            : [];
}

public static class Economics
{
    public static Tariff? TariffFor(string date, IReadOnlyList<Tariff> tariffs) =>
        tariffs.Where(t => string.CompareOrdinal(t.From, date) <= 0).MaxBy(t => t.From);

    public static double? Savings(DayEnergy day, IReadOnlyList<Tariff> tariffs)
    {
        if (TariffFor(day.Date, tariffs) is not { } tariff)
            return null;
        return Math.Max(day.LoadKwh - day.ImportKwh, 0) * tariff.ImportEurPerKwh + day.ExportKwh * tariff.FeedInEurPerKwh;
    }

    public static double? Sum(IEnumerable<DayEnergy> days, IReadOnlyList<Tariff> tariffs)
    {
        double? total = null;
        foreach (var day in days)
            if (Savings(day, tariffs) is { } value)
                total = (total ?? 0) + value;
        return total is null ? null : Math.Round(total.Value, 2);
    }

    /// <summary>Hochrechnung auf ein Jahr. Unter 30 Messtagen wäre sie nur Zufall, ohne Sommer bleibt sie grob.</summary>
    public static double? PaybackYears(double? totalSavings, int measuredDays, decimal? investment)
    {
        if (totalSavings is not > 0 || investment is not > 0 || measuredDays < 30)
            return null;
        return Math.Round((double)investment.Value / (totalSavings.Value / measuredDays * 365), 1);
    }
}
