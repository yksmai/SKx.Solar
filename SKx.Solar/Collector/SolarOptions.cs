using System.Text.Json.Serialization;

namespace SKx.Solar.Collector;

/// <summary>Alle Erweiterungen sind optional: Ohne Eintrag bleiben sie aus und es verlässt nichts das Heimnetz.</summary>
public sealed class SolarOptions
{
    /// <summary>Adresse des Solarman-Loggers. Leer = noch nicht eingerichtet, die Datensammlung wartet.</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 8899;
    /// <summary>Leer = per UDP-Suche ermitteln.</summary>
    public uint? LoggerSerial { get; set; }
    public int PollSeconds { get; set; } = 10;
    public int RawRetentionDays { get; set; } = 30;
    /// <summary>Schont die Karte: schreibt alle 5 Minuten statt jede Minute und behält nur einen Rohwert pro Minute.</summary>
    public bool SdCardMode { get; set; } = true;
    public string DatabasePath { get; set; } = "data/solar.db";
    public double? BatteryCapacityKwh { get; set; }
    public decimal? InvestmentEur { get; set; }
    public List<Tariff> Tariffs { get; set; } = [];
    public AlertOptions Alerts { get; set; } = new();
    public ForecastOptions Forecast { get; set; } = new();
    public PriceOptions DynamicPrices { get; set; } = new();
    public MqttOptions Mqtt { get; set; } = new();
    public BackupOptions Backup { get; set; } = new();
    public BalconyOptions Balcony { get; set; } = new();
    public AuthOptions Auth { get; set; } = new();
    public UpstreamOptions Upstream { get; set; } = new();
    public SyncOptions Sync { get; set; } = new();
    public MirrorOptions Mirror { get; set; } = new();
}

/// <summary>Auf dem Pi: Wohin er seine Daten schickt.</summary>
public sealed class SyncOptions
{
    public string? Url { get; set; }
    public string? Token { get; set; }
    public int IntervalSeconds { get; set; } = 30;
    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>Auf dem Server: Diese Instanz liest nichts selbst, sondern zeigt nur an, was der Pi schickt.</summary>
public sealed class MirrorOptions
{
    public bool Enabled { get; set; }
    public string? Token { get; set; }
}

public sealed class UpstreamOptions
{
    /// <summary>Nur auf dem PC setzen, z. B. http://raspberrypi.local:8080. Leer = diese Instanz liest selbst (der Pi).</summary>
    public string? Url { get; set; }
}

public sealed class Tariff
{
    /// <summary>Gültig ab diesem Tag (yyyy-MM-dd), bis ein neuerer Eintrag beginnt.</summary>
    public string From { get; set; } = "2000-01-01";
    public double ImportEurPerKwh { get; set; }
    public double FeedInEurPerKwh { get; set; }
}

public sealed class AlertOptions
{
    public double BatteryLowPercent { get; set; } = 15;
    public double InverterTempLimitC { get; set; } = 75;
    public string MinNotifySeverity { get; set; } = "warning";
    public string? NtfyServer { get; set; } = "https://ntfy.sh";
    public string? NtfyTopic { get; set; }
    public string? TelegramBotToken { get; set; }
    public string? TelegramChatId { get; set; }
}

public sealed class ForecastOptions
{
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public double PerformanceRatio { get; set; } = 0.8;
    public List<PvPlane> Planes { get; set; } = [];
    [JsonIgnore]
    public bool Enabled => Latitude is not null && Longitude is not null && Planes.Count > 0;
}

public sealed class PvPlane
{
    public double TiltDegrees { get; set; } = 30;
    /// <summary>0 = Süd, -90 = Ost, 90 = West.</summary>
    public double AzimuthDegrees { get; set; }
    public double Kwp { get; set; }
}

public sealed class PriceOptions
{
    public bool Enabled { get; set; }
    public string Country { get; set; } = "de";
    /// <summary>Netzentgelte, Abgaben und Marge in ct/kWh, ohne sie zeigt die Anzeige nur den Börsenpreis.</summary>
    public double SurchargeCtPerKwh { get; set; }
    public double VatPercent { get; set; }
}

public sealed class MqttOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 1883;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string Topic { get; set; } = "skx-solar/state";
    /// <summary>Meldet alle Sensoren per MQTT-Discovery, Home Assistant legt sie dann selbst an.</summary>
    public bool HomeAssistant { get; set; } = true;
    public bool Enabled => !string.IsNullOrWhiteSpace(Host);
}

public sealed class BackupOptions
{
    /// <summary>Leer = Ordner "backups" neben der Datenbank. Schützt erst auf einem NAS auch vor einem Kartenausfall.</summary>
    public string? Directory { get; set; }
    public int KeepDays { get; set; } = 14;
    public int Hour { get; set; } = 3;
    public bool Enabled { get; set; } = true;
}

public sealed class BalconyOptions
{
    /// <summary>APsystems EZ1 im lokalen Modus. Leer = kein Balkonkraftwerk.</summary>
    public string? Host { get; set; }
    public int Port { get; set; } = 8050;
}

public sealed class AuthOptions
{
    /// <summary>Das frühere einzelne Passwort, wird zum Konto "admin". Erzeugen mit: SKx.Solar --hash-password</summary>
    public string? PasswordHash { get; set; }
    public List<AuthUser> Users { get; set; } = [];
    public bool Enabled => Users.Count > 0;

    public AuthUser? Find(string? name) =>
        Users.Find(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    public void Migrate()
    {
        if (Users.Count == 0 && !string.IsNullOrWhiteSpace(PasswordHash))
            Users = [new AuthUser { Name = Roles.Admin, Role = Roles.Admin, PasswordHash = PasswordHash }];
        PasswordHash = null;
    }
}

public static class Roles
{
    public const string Admin = "admin";
    public const string Viewer = "viewer";
}

public sealed class AuthUser
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;
    public string PasswordHash { get; set; } = "";
}
