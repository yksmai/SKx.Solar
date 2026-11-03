using SKx.Solar.Deye;

namespace SKx.Solar.Alerts;

public static class Severity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Critical = "critical";

    public static int Rank(string severity) => severity switch { Critical => 2, Warning => 1, _ => 0 };
}

/// <param name="HoldSeconds">So lange muss die Bedingung anhalten, bevor sie zur Meldung wird.</param>
public sealed record AlertCondition(string Key, string Severity, string Title, string Message, int HoldSeconds = 0);

public sealed record AlertInput(
    InverterSample? Sample,
    long Now,
    long StartedAt,
    int LocalHour,
    double BatteryLowPercent,
    double InverterTempLimitC,
    double? MedianPvKwh,
    double? TodayPvKwh,
    string? BackupError,
    SystemHealth? System = null);

/// <param name="BackupAgeSeconds">Alter der neuesten Sicherung, null solange der Zeitraum noch nichts aussagt.</param>
public sealed record SystemHealth(double? CpuTempC, bool? UnderVoltage, double? DiskFreeGb, long? BackupAgeSeconds);

public static class AlertRules
{
    public const int NoDataSeconds = 300;
    public const double CpuHotC = 80;
    public const double DiskLowGb = 2;
    public const long BackupStaleSeconds = 2 * 86400;
    private const string NoData = "no_data";
    private const string BatteryLow = "battery_low";
    private const string BatteryFull = "battery_full";
    private static readonly string[] MeasuredKeys = ["inverter_fault", BatteryLow, BatteryFull, "inverter_hot", "string_idle", "pv_low"];

    /// <summary>Alle gerade erfüllten Bedingungen. Offene Meldungen schließen teils später, damit sie nicht flackern.</summary>
    public static List<AlertCondition> Evaluate(AlertInput input, IReadOnlySet<string> open)
    {
        var result = new List<AlertCondition>();
        var s = input.Sample;

        var age = s is null ? input.Now - input.StartedAt : input.Now - s.Ts;
        if (age >= NoDataSeconds)
            result.Add(new(NoData, Severity.Critical, "Keine Daten vom Wechselrichter",
                $"Seit {age / 60} Minuten kommen keine Messwerte an. Logger und Netzwerk prüfen."));

        if (input.BackupError is { } backupError)
            result.Add(new("backup_failed", Severity.Warning, "Datensicherung fehlgeschlagen", backupError));

        if (input.System is { } system)
        {
            if (system.BackupAgeSeconds >= BackupStaleSeconds)
                result.Add(new("backup_stale", Severity.Warning, "Sicherung zu alt",
                    $"Die letzte Sicherung ist {system.BackupAgeSeconds / 86400} Tage alt. NAS und Sicherungsziel prüfen."));
            if (system.CpuTempC >= CpuHotC)
                result.Add(new("pi_hot", Severity.Warning, "Raspberry Pi sehr warm",
                    $"CPU bei {system.CpuTempC:0} °C. Belüftung im Schrank prüfen.", HoldSeconds: 300));
            if (system.UnderVoltage == true)
                result.Add(new("pi_undervoltage", Severity.Warning, "Raspberry Pi hat Unterspannung",
                    "Das Netzteil liefert zu wenig. Das gefährdet die SD-Karte, bitte Netzteil und Kabel prüfen.", HoldSeconds: 60));
            if (system.DiskFreeGb < DiskLowGb)
                result.Add(new("disk_low", Severity.Warning, "Speicher fast voll", $"Noch {system.DiskFreeGb:0.0} GB frei."));
        }

        if (s is null || age >= NoDataSeconds)
        {
            // Ohne Messwerte lässt sich nichts widerlegen. Offenes bleibt offen, sonst käme es nach dem Ausfall erneut als neue Meldung.
            result.AddRange(MeasuredKeys.Where(open.Contains).Select(key => new AlertCondition(key, Severity.Info, "", "")));
            return result;
        }

        if (s.State is 3 or 4 || s.FaultCodes.Length > 0)
            result.Add(new("inverter_fault", s.State == 4 ? Severity.Critical : Severity.Warning,
                $"Wechselrichter meldet: {DeyeDecoder.StateName(s.State)}",
                s.FaultCodes.Length > 0 ? s.FaultCodes : "Das Gerät liefert keinen Fehlercode."));

        if (s.BatterySoc <= input.BatteryLowPercent || (open.Contains(BatteryLow) && s.BatterySoc < input.BatteryLowPercent + 5))
            // Nur ein Hinweis: Eine bis zur Reserve entladene Batterie ist abends der Normalfall und kein Grund für eine Push-Nachricht.
            result.Add(new(BatteryLow, Severity.Info, "Batterie fast leer", $"Ladestand {s.BatterySoc:0} %."));

        if (s.BatterySoc >= 100 || (open.Contains(BatteryFull) && s.BatterySoc >= 97))
            result.Add(new(BatteryFull, Severity.Info, "Batterie voll", "Überschuss geht jetzt ins Netz oder wird abgeregelt."));

        if (s.InverterTempC >= input.InverterTempLimitC)
            result.Add(new("inverter_hot", Severity.Warning, "Wechselrichter sehr warm",
                $"{s.InverterTempC:0.0} °C. Belüftung prüfen."));

        // Ein String hat Spannung, liefert aber keinen Strom, während der andere arbeitet.
        if (IsIdle(s.Pv1V, s.Pv1A, s.Pv2A) || IsIdle(s.Pv2V, s.Pv2A, s.Pv1A))
        {
            var which = IsIdle(s.Pv1V, s.Pv1A, s.Pv2A) ? 1 : 2;
            result.Add(new("string_idle", Severity.Warning, $"String {which} liefert keinen Strom",
                "Der String hat Spannung, aber keinen Strom, während der andere arbeitet. Verschattung oder Anschluss prüfen.",
                HoldSeconds: 900));
        }

        if (input.LocalHour >= 18 && input.MedianPvKwh is > 1 and var median && input.TodayPvKwh is { } today && today < median * 0.4)
            result.Add(new("pv_low", Severity.Info, "Schwacher Ertrag heute",
                $"{today:0.0} kWh statt typischer {median:0.0} kWh der letzten 14 Tage."));

        return result;
    }

    private static bool IsIdle(double volts, double amps, double otherAmps) => volts > 100 && amps < 0.1 && otherAmps >= 1;
}

/// <summary>Merkt sich, seit wann Bedingungen anstehen, und entscheidet, welche Meldungen sich öffnen oder schließen.</summary>
public sealed class AlertTracker
{
    /// <summary>Das Signal des Pi schwankt mit der Last. Erst nach dieser Ruhezeit gilt die Unterspannung als behoben.</summary>
    private static readonly Dictionary<string, long> CloseAfterSeconds = new() { ["pi_undervoltage"] = 1800 };
    private readonly Dictionary<string, long> _pendingSince = [];
    private readonly Dictionary<string, long> _missingSince = [];

    public (List<AlertCondition> Opened, List<string> Closed) Step(IEnumerable<AlertCondition> conditions, IReadOnlySet<string> open, long now)
    {
        var active = conditions.ToDictionary(c => c.Key);
        foreach (var key in active.Keys)
            _missingSince.Remove(key);
        foreach (var key in _pendingSince.Keys.Where(k => !active.ContainsKey(k)).ToList())
            _pendingSince.Remove(key);

        var opened = new List<AlertCondition>();
        foreach (var condition in active.Values.Where(c => !open.Contains(c.Key)))
        {
            var since = _pendingSince.TryGetValue(condition.Key, out var value) ? value : _pendingSince[condition.Key] = now;
            if (now - since >= condition.HoldSeconds)
            {
                opened.Add(condition);
                _pendingSince.Remove(condition.Key);
            }
        }
        return (opened, open.Where(k => !active.ContainsKey(k) && QuietLongEnough(k, now)).ToList());
    }

    private bool QuietLongEnough(string key, long now)
    {
        if (!CloseAfterSeconds.TryGetValue(key, out var seconds))
            return true;
        var since = _missingSince.TryGetValue(key, out var value) ? value : _missingSince[key] = now;
        if (now - since < seconds)
            return false;
        _missingSince.Remove(key);
        return true;
    }
}
