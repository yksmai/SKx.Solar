using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SKx.Solar.Alerts;
using SKx.Solar.Collector;
using SKx.Solar.Security;

namespace SKx.Solar.Integrations;

/// <summary>Verbindung zum Logger des Wechselrichters.</summary>
public sealed class InverterSettings
{
    public string? Host { get; set; }
    public int Port { get; set; } = 8899;
    /// <summary>Leer = per UDP-Suche ermitteln.</summary>
    public uint? LoggerSerial { get; set; }
}

/// <summary>Der Teil der Einstellungen, der sich im Dashboard ändern lässt. Zugangsdaten gehören nicht dazu.</summary>
public sealed class PlanningSettings
{
    /// <summary>Null heißt unverändert: Ältere gespeicherte Stände ohne diesen Teil überschreiben appsettings.json nicht.</summary>
    public InverterSettings? Inverter { get; set; }
    public BackupOptions? Backup { get; set; }
    public double? BatteryCapacityKwh { get; set; }
    public decimal? InvestmentEur { get; set; }
    public List<Tariff> Tariffs { get; set; } = [];
    public ForecastOptions Forecast { get; set; } = new();
    public PriceOptions DynamicPrices { get; set; } = new();
    public AlertOptions Alerts { get; set; } = new();
    public MqttOptions Mqtt { get; set; } = new();
    public BalconyOptions Balcony { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
    public bool SdCardMode { get; set; } = true;
    /// <summary>Nur in Antworten gesetzt: Geheimnisse verlassen den Server nie, die Oberfläche sieht nur, ob es sie gibt.</summary>
    public bool TelegramTokenSet { get; set; }
    public bool MqttPasswordSet { get; set; }
    public bool SyncTokenSet { get; set; }

    public static PlanningSettings From(SolarOptions o) => new()
    {
        Inverter = new() { Host = o.Host, Port = o.Port, LoggerSerial = o.LoggerSerial },
        Backup = o.Backup,
        BatteryCapacityKwh = o.BatteryCapacityKwh,
        InvestmentEur = o.InvestmentEur,
        Tariffs = o.Tariffs,
        Forecast = o.Forecast,
        DynamicPrices = o.DynamicPrices,
        Alerts = o.Alerts,
        Mqtt = o.Mqtt,
        Balcony = o.Balcony,
        Sync = o.Sync,
        SdCardMode = o.SdCardMode,
    };

    /// <summary>Kopie für die Oberfläche, ohne Token und Passwort.</summary>
    public PlanningSettings Masked() => new()
    {
        Inverter = Inverter,
        Backup = Backup,
        BatteryCapacityKwh = BatteryCapacityKwh,
        InvestmentEur = InvestmentEur,
        Tariffs = Tariffs,
        Forecast = Forecast,
        DynamicPrices = DynamicPrices,
        Balcony = Balcony,
        SdCardMode = SdCardMode,
        TelegramTokenSet = !string.IsNullOrEmpty(Alerts.TelegramBotToken),
        MqttPasswordSet = !string.IsNullOrEmpty(Mqtt.Password),
        SyncTokenSet = !string.IsNullOrEmpty(Sync.Token),
        Sync = new() { Url = Sync.Url, IntervalSeconds = Sync.IntervalSeconds },
        Alerts = new()
        {
            BatteryLowPercent = Alerts.BatteryLowPercent,
            InverterTempLimitC = Alerts.InverterTempLimitC,
            MinNotifySeverity = Alerts.MinNotifySeverity,
            NtfyServer = Alerts.NtfyServer,
            NtfyTopic = Alerts.NtfyTopic,
            TelegramChatId = Alerts.TelegramChatId,
        },
        Mqtt = new() { Host = Mqtt.Host, Port = Mqtt.Port, User = Mqtt.User, Topic = Mqtt.Topic, HomeAssistant = Mqtt.HomeAssistant },
    };

    /// <summary>Null heißt unverändert, leer heißt entfernen. So überschreibt ein Formular ohne Eingabe kein Geheimnis.</summary>
    public void KeepSecretsFrom(PlanningSettings current)
    {
        Alerts.TelegramBotToken = Secret(Alerts.TelegramBotToken, current.Alerts.TelegramBotToken);
        Mqtt.Password = Secret(Mqtt.Password, current.Mqtt.Password);
        Sync.Token = Secret(Sync.Token, current.Sync.Token);
        static string? Secret(string? incoming, string? existing) =>
            incoming is null ? existing : incoming.Length == 0 ? null : incoming;
    }

    public void ApplyTo(SolarOptions o)
    {
        if (Inverter is not null)
        {
            o.Host = Inverter.Host;
            o.Port = Inverter.Port;
            o.LoggerSerial = Inverter.LoggerSerial;
        }
        if (Backup is not null)
            o.Backup = Backup;
        o.BatteryCapacityKwh = BatteryCapacityKwh;
        o.InvestmentEur = InvestmentEur;
        o.Tariffs = Tariffs.OrderBy(t => t.From, StringComparer.Ordinal).ToList();
        o.Forecast = Forecast;
        o.DynamicPrices = DynamicPrices;
        o.Alerts = Alerts;
        o.Mqtt = Mqtt;
        o.Balcony = Balcony;
        o.Sync = Sync;
        o.SdCardMode = SdCardMode;
    }

    /// <summary>Liefert die erste Beanstandung als Text für die Anzeige, null wenn alles passt.</summary>
    public string? Validate()
    {
        if (Tariffs is null || Forecast is null || Forecast.Planes is null || DynamicPrices is null || Alerts is null || Mqtt is null || Balcony is null || Sync is null)
            return "Die Einstellungen sind unvollständig.";
        if (Inverter is { } inverter)
        {
            if (!string.IsNullOrWhiteSpace(inverter.Host) && Uri.CheckHostName(inverter.Host) == UriHostNameType.Unknown)
                return "Die Adresse des Wechselrichters ist ungültig.";
            if (inverter.Port is < 1 or > 65535)
                return "Der Port des Wechselrichters muss zwischen 1 und 65535 liegen.";
            if (inverter.LoggerSerial == 0)
                return "Die Seriennummer des Loggers darf nicht 0 sein.";
        }
        if (Backup is { } backup)
        {
            if (!string.IsNullOrWhiteSpace(backup.Directory) && !Path.IsPathFullyQualified(backup.Directory))
                return "Der Sicherungsordner braucht einen vollständigen Pfad, z. B. /mnt/nas/solar.";
            if (backup.KeepDays is < 1 or > 3650)
                return "Sicherungen müssen zwischen 1 und 3650 Tagen aufbewahrt werden.";
            if (backup.Hour is < 0 or > 23)
                return "Die Uhrzeit der Sicherung muss zwischen 0 und 23 liegen.";
        }
        if (BatteryCapacityKwh is <= 0 or > 1000)
            return "Die Batteriekapazität muss zwischen 0 und 1000 kWh liegen.";
        if (InvestmentEur is < 0 or > 10_000_000)
            return "Die Investition muss zwischen 0 und 10 Mio. € liegen.";

        if (Tariffs.Count > 100)
            return "Es sind höchstens 100 Tarife möglich.";
        foreach (var tariff in Tariffs)
        {
            if (tariff is null || !DateOnly.TryParseExact(tariff.From, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                return "Jeder Tarif braucht ein gültiges Startdatum.";
            if (tariff.ImportEurPerKwh is < 0 or > 5 || tariff.FeedInEurPerKwh is < 0 or > 5)
                return "Tarifpreise müssen zwischen 0 und 500 ct/kWh liegen.";
        }
        if (Tariffs.Select(t => t.From).Distinct().Count() != Tariffs.Count)
            return "Zwei Tarife beginnen am selben Tag.";

        var f = Forecast;
        if (f.Latitude is null != f.Longitude is null)
            return "Für den Standort fehlt Breite oder Länge.";
        if (f.Latitude is < -90 or > 90 || f.Longitude is < -180 or > 180)
            return "Der Standort liegt außerhalb gültiger Koordinaten.";
        if (f.PerformanceRatio is <= 0 or > 1)
            return "Der Verlustfaktor muss über 0 und höchstens 1 sein.";
        if (f.Planes.Count > 20)
            return "Es sind höchstens 20 Dachflächen möglich.";
        if (f.Planes.Count > 0 && f.Latitude is null)
            return "Für die Dachflächen fehlt der Standort.";
        foreach (var plane in f.Planes)
        {
            if (plane is null || plane.TiltDegrees is < 0 or > 90)
                return "Die Neigung muss zwischen 0 und 90 Grad liegen.";
            if (plane.AzimuthDegrees is < -180 or > 180)
                return "Die Ausrichtung muss zwischen -180 und 180 Grad liegen.";
            if (plane.Kwp is <= 0 or > 1000)
                return "Die Leistung einer Dachfläche muss zwischen 0 und 1000 kWp liegen.";
        }

        var p = DynamicPrices;
        if (p.Country is not ("de" or "at"))
            return "Börsenpreise gibt es nur für Deutschland und Österreich.";
        if (p.SurchargeCtPerKwh is < 0 or > 500)
            return "Der Aufschlag muss zwischen 0 und 500 ct/kWh liegen.";
        if (p.VatPercent is < 0 or > 100)
            return "Die Umsatzsteuer muss zwischen 0 und 100 % liegen.";

        var a = Alerts;
        if (a.BatteryLowPercent is < 0 or > 100)
            return "Die Batteriewarnung muss zwischen 0 und 100 % liegen.";
        if (a.InverterTempLimitC is < 30 or > 120)
            return "Die Temperaturgrenze muss zwischen 30 und 120 °C liegen.";
        if (a.MinNotifySeverity is not (Severity.Info or Severity.Warning or Severity.Critical))
            return "Unbekannte Dringlichkeit für Benachrichtigungen.";
        if (!string.IsNullOrWhiteSpace(a.NtfyTopic)
            && !(Uri.TryCreate(a.NtfyServer, UriKind.Absolute, out var server) && server.Scheme is "http" or "https"))
            return "Der ntfy-Server braucht eine vollständige Adresse mit http oder https.";

        var m = Mqtt;
        if (m.Port is < 1 or > 65535)
            return "Der MQTT-Port muss zwischen 1 und 65535 liegen.";
        if (string.IsNullOrWhiteSpace(m.Topic) || m.Topic.IndexOfAny(['#', '+']) >= 0)
            return "Das MQTT-Thema darf nicht leer sein und keine Platzhalter enthalten.";

        if (!string.IsNullOrWhiteSpace(Balcony.Host) && Uri.CheckHostName(Balcony.Host) == UriHostNameType.Unknown)
            return "Die Adresse des Balkonkraftwerks ist ungültig.";
        if (Balcony.Port is < 1 or > 65535)
            return "Der Port des Balkonkraftwerks muss zwischen 1 und 65535 liegen.";

        // Das Token darf nicht unverschlüsselt durchs Internet gehen.
        if (!string.IsNullOrWhiteSpace(Sync.Url)
            && !(Uri.TryCreate(Sync.Url, UriKind.Absolute, out var mirror) && (mirror.Scheme == "https" || (mirror.Scheme == "http" && mirror.IsLoopback))))
            return "Der Spiegel-Server braucht eine vollständige Adresse mit https.";
        if (Sync.IntervalSeconds is < 5 or > 3600)
            return "Das Sync-Intervall muss zwischen 5 und 3600 Sekunden liegen.";
        return null;
    }
}

/// <summary>Weckt Dienste nach einer Änderung, damit neue Einstellungen ohne Neustart gelten.</summary>
public sealed class SettingsSignal
{
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Vor der Arbeit abholen: So geht eine Änderung währenddessen nicht verloren.</summary>
    public Task Changed => Volatile.Read(ref _changed).Task;

    public void Raise() =>
        Interlocked.Exchange(ref _changed, new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public static async Task WaitAsync(Task changed, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await changed.WaitAsync(timeout, cancellationToken);
        }
        catch (TimeoutException)
        {
        }
    }
}

/// <summary>Legt die Änderungen aus dem Dashboard neben der Datenbank ab, sie haben Vorrang vor appsettings.json.</summary>
public sealed class SettingsStore(IOptions<SolarOptions> options, SettingsSignal signal)
{
    private const string FileName = "settings.json";
    private const string AuthFileName = "auth.json";
    public const int MinPasswordLength = 8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly Lock _gate = new();

    public PlanningSettings Current => PlanningSettings.From(options.Value);

    public void Save(PlanningSettings settings)
    {
        var path = PathFor(options.Value.DatabasePath);
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Erst vollständig schreiben: Ein Stromausfall hinterlässt so keine halbe Datei.
            settings.KeepSecretsFrom(Current);
            WritePrivate(path, JsonSerializer.Serialize(settings, Json));
            settings.ApplyTo(options.Value);
        }
        signal.Raise();
    }

    /// <summary>Legt beim ersten Login das Adminkonto an, nur die erste Anfrage gewinnt.</summary>
    public bool CreateFirstUser(string name, string password)
    {
        var hash = PasswordHash.Create(password);
        lock (_gate)
        {
            if (options.Value.Auth.Enabled)
                return false;
            WriteUsers([new AuthUser { Name = name, Role = Roles.Admin, PasswordHash = hash }]);
        }
        return true;
    }

    /// <summary>Liefert die Beanstandung als Text für die Anzeige, null wenn das Konto angelegt ist.</summary>
    public string? AddUser(string? name, string? password, string? role)
    {
        if ((NameProblem(name) ?? PasswordProblem(password) ?? RoleProblem(role)) is { } problem)
            return problem;
        var hash = PasswordHash.Create(password!);
        lock (_gate)
        {
            var auth = options.Value.Auth;
            if (auth.Find(name) is not null)
                return "Diesen Benutzernamen gibt es schon.";
            WriteUsers([.. auth.Users, new AuthUser { Name = name!, Role = role!, PasswordHash = hash }]);
        }
        return null;
    }

    /// <summary>Null heißt unverändert. Liefert die Beanstandung als Text für die Anzeige.</summary>
    public string? UpdateUser(string name, string? role, string? password)
    {
        if (((role is null ? null : RoleProblem(role)) ?? (password is null ? null : PasswordProblem(password))) is { } problem)
            return problem;
        var hash = password is null ? null : PasswordHash.Create(password);
        lock (_gate)
        {
            var auth = options.Value.Auth;
            if (auth.Find(name) is not { } user)
                return "Diesen Benutzer gibt es nicht mehr.";
            var changed = new AuthUser { Name = user.Name, Role = role ?? user.Role, PasswordHash = hash ?? user.PasswordHash };
            List<AuthUser> users = [.. auth.Users.Select(u => u == user ? changed : u)];
            if (!users.Exists(u => u.Role == Roles.Admin))
                return "Mindestens ein Konto muss Admin bleiben.";
            WriteUsers(users);
        }
        return null;
    }

    public string? RemoveUser(string name)
    {
        lock (_gate)
        {
            var auth = options.Value.Auth;
            if (auth.Find(name) is not { } user)
                return "Diesen Benutzer gibt es nicht mehr.";
            List<AuthUser> users = [.. auth.Users.Where(u => u != user)];
            if (!users.Exists(u => u.Role == Roles.Admin))
                return "Mindestens ein Konto muss Admin bleiben.";
            WriteUsers(users);
        }
        return null;
    }

    public static string? NameProblem(string? name) =>
        string.IsNullOrEmpty(name) || name.Length > 32 || !name.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_')
            ? "Der Benutzername braucht 1 bis 32 Zeichen: Buchstaben, Ziffern, Punkt, Binde- oder Unterstrich."
            : null;

    public static string? PasswordProblem(string? password) =>
        (password ?? "").Length < MinPasswordLength ? $"Das Passwort braucht mindestens {MinPasswordLength} Zeichen." : null;

    private static string? RoleProblem(string? role) =>
        role is Roles.Admin or Roles.Viewer ? null : "Unbekannte Rolle.";

    // Liste immer ersetzen, nie ändern: Anfragen lesen sie ohne Sperre.
    private void WriteUsers(List<AuthUser> users)
    {
        var path = Path.Combine(Path.GetDirectoryName(PathFor(options.Value.DatabasePath))!, AuthFileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WritePrivate(path, JsonSerializer.Serialize(new AuthOptions { Users = users }, Json));
        options.Value.Auth.Users = users;
    }

    private static void WritePrivate(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        // Enthält Token und Passwort-Hash, darf also nur der Dienst lesen.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    public static string PathFor(string databasePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, FileName);

    public static void ApplySaved(SolarOptions o)
    {
        var path = PathFor(o.DatabasePath);
        var authPath = Path.Combine(Path.GetDirectoryName(path)!, AuthFileName);
        try
        {
            if (File.Exists(authPath) && JsonSerializer.Deserialize<AuthOptions>(File.ReadAllText(authPath), Json) is { } auth)
            {
                auth.Migrate();
                if (auth.Enabled)
                    o.Auth = auth;
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"{authPath} wird ignoriert: {ex.Message}");
        }
        o.Auth.Migrate();
        if (!File.Exists(path))
            return;
        try
        {
            var saved = JsonSerializer.Deserialize<PlanningSettings>(File.ReadAllText(path), Json);
            if (saved?.Validate() is null)
                saved?.ApplyTo(o);
            else
                Console.Error.WriteLine($"{path} wird ignoriert: {saved.Validate()}");
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Die Datensammlung ist wichtiger als die Planung, darum startet die App trotzdem.
            Console.Error.WriteLine($"{path} wird ignoriert: {ex.Message}");
        }
    }

    /// <summary>Die Sicherung soll auch Einstellungen und Passwort enthalten. Ohne Passwort könnte nach einer Wiederherstellung jeder im Netz eines setzen.</summary>
    public static void CopyTo(string databasePath, string directory)
    {
        var folder = Path.GetDirectoryName(PathFor(databasePath))!;
        foreach (var name in new[] { FileName, AuthFileName })
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
                File.Copy(path, Path.Combine(directory, "solar-" + name), overwrite: true);
        }
    }

    /// <summary>Kennzeichnet Passwort und Rolle in der Anmeldung. Ändert sich eines davon, passen ältere Anmeldungen nicht mehr.</summary>
    public static string AccountStamp(AuthUser user) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(user.Role + ":" + user.PasswordHash)))[..16];
}
