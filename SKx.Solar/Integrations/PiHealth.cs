using System.Globalization;
using Microsoft.Extensions.Hosting.Systemd;
using SKx.Solar.Collector;

namespace SKx.Solar.Integrations;

/// <param name="WrittenMbPerDay">Schreiblast der Karte seit dem Systemstart, auf einen Tag hochgerechnet.</param>
public sealed record PiHealthInfo(double? CpuTempC, double? Load1, long? MemAvailableMb, bool? UnderVoltage, double? WrittenMbPerDay);

/// <summary>Liest den Zustand des Pi aus /proc und /sys. Auf anderen Systemen bleiben die Werte leer.</summary>
public static class PiHealth
{
    private const string Hwmon = "sys/devices/platform/soc/soc:firmware/raspberrypi-hwmon/hwmon";

    public static PiHealthInfo Read(string root = "/")
    {
        var temp = Number(Text(root, "sys/class/thermal/thermal_zone0/temp"));
        var load = Number(Text(root, "proc/loadavg")?.Split(' ')[0]);
        var memory = Text(root, "proc/meminfo")?.Split('\n').FirstOrDefault(l => l.StartsWith("MemAvailable:", StringComparison.Ordinal));
        var available = Number(memory?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return new(
            temp / 1000,
            load,
            available is null ? null : (long)(available.Value / 1024),
            UnderVoltage(root),
            WrittenMbPerDay(root));
    }

    private static bool? UnderVoltage(string root)
    {
        var directory = Path.Combine(root, Hwmon);
        if (!Directory.Exists(directory))
            return null;
        var alarm = Directory.GetDirectories(directory).Select(d => Text(d, "in0_lcrit_alarm")).FirstOrDefault(v => v is not null);
        return alarm is null ? null : alarm == "1";
    }

    private static double? WrittenMbPerDay(string root)
    {
        // Feld 7 von block/stat: geschriebene Sektoren zu 512 Byte.
        var stat = Text(root, "sys/block/mmcblk0/stat")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var uptime = Number(Text(root, "proc/uptime")?.Split(' ')[0]);
        // Kurz nach dem Start verzerren Installationen und Updates die Hochrechnung zu stark.
        if (stat is not { Length: > 6 } || uptime is not >= 86400 || Number(stat[6]) is not { } sectors)
            return null;
        return Math.Round(sectors * 512 / 1048576 / uptime.Value * 86400);
    }

    private static string? Text(string root, string path)
    {
        try
        {
            return File.ReadAllText(Path.Combine(root, path)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static double? Number(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
}

/// <summary>Meldet systemd, dass die Datensammlung läuft. Bleibt das aus, startet systemd den Dienst neu.</summary>
public sealed class WatchdogService(LiveState live, IServiceProvider services) : BackgroundService
{
    private const int MaxSilenceSeconds = 300;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var notifier = services.GetService<ISystemdNotifier>();
        if (notifier is null || !long.TryParse(Environment.GetEnvironmentVariable("WATCHDOG_USEC"), out var usec) || usec <= 0)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromMicroseconds(usec / 3.0));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - live.Heartbeat < MaxSilenceSeconds)
                notifier.Notify(new ServiceState("WATCHDOG=1"));
        }
    }
}
