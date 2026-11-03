using SKx.Solar.Collector;
using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

public sealed record RoofCheckResult(bool Configured, string Verdict, int ClearDays, int NeededDays, double? ShiftMinutes);

/// <summary>Vergleicht an klaren Tagen, wann das Dach im Mittel erzeugt, mit dem, was die eingetragenen Flächen erwarten lassen.</summary>
public static class RoofCheck
{
    public const int NeededDays = 3;
    private const double ToleranceMinutes = 20;
    private const double MaxVariation = 2.6;
    private const double MinPeakW = 100;
    private const int MinBins = 12;
    // Ab hier kann die volle Batterie die Anlage abgeregelt haben, das verschiebt die Kurve.
    private const double FullSoc = 98;

    public static RoofCheckResult Evaluate(IReadOnlyList<RoofMinute> minutes, ForecastOptions forecast)
    {
        if (!forecast.Enabled)
            return new(false, "waiting", 0, NeededDays, null);

        var shifts = minutes.GroupBy(m => m.LocalDate)
            .Where(day => IsClear(day.ToList()))
            .Select(day => Shift(day.ToList(), forecast))
            .OfType<double>()
            .ToList();
        if (shifts.Count < NeededDays)
            return new(true, "waiting", shifts.Count, NeededDays, null);

        var shift = Math.Round(shifts.Average());
        var verdict = Math.Abs(shift) <= ToleranceMinutes ? "ok" : shift > 0 ? "later" : "earlier";
        return new(true, verdict, shifts.Count, NeededDays, shift);
    }

    /// <summary>Eine klare Kurve steigt einmal und fällt einmal: Die Summe aller Sprünge bleibt nahe der doppelten Spitze.</summary>
    public static bool IsClear(IReadOnlyList<RoofMinute> day)
    {
        if (day.Count == 0 || day.Max(m => m.BatterySoc) >= FullSoc)
            return false;
        var bins = day.GroupBy(m => m.Ts / 900).OrderBy(b => b.Key).Select(b => b.Average(m => m.PvW)).ToList();
        var peak = bins.Max();
        if (bins.Count < MinBins || peak < MinPeakW)
            return false;
        var variation = bins[0] + bins[^1] + bins.Zip(bins.Skip(1), (a, b) => Math.Abs(b - a)).Sum();
        return variation / peak <= MaxVariation;
    }

    private static double? Shift(IReadOnlyList<RoofMinute> day, ForecastOptions forecast)
    {
        double measuredSum = 0, measuredTime = 0, expectedSum = 0, expectedTime = 0;
        foreach (var minute in day)
        {
            var expected = Power(minute.Ts, forecast);
            measuredSum += minute.PvW;
            measuredTime += minute.PvW * minute.Ts;
            expectedSum += expected;
            expectedTime += expected * minute.Ts;
        }
        if (measuredSum <= 0 || expectedSum <= 0)
            return null;
        return (measuredTime / measuredSum - expectedTime / expectedSum) / 60;
    }

    /// <summary>Relative Leistung bei klarem Himmel. Für den Zeitpunkt des Schwerpunkts genügt ein einfaches Sonnenmodell.</summary>
    public static double Power(long ts, ForecastOptions forecast)
    {
        if (forecast.Latitude is not { } latitude || forecast.Longitude is not { } longitude)
            return 0;
        var time = DateTimeOffset.FromUnixTimeSeconds(ts);
        var angle = 2 * Math.PI * (time.DayOfYear - 81) / 364;
        var declination = Radians(23.45) * Math.Sin(2 * Math.PI * (284 + time.DayOfYear) / 365);
        var equationMinutes = 9.87 * Math.Sin(2 * angle) - 7.53 * Math.Cos(angle) - 1.5 * Math.Sin(angle);
        var solarHours = time.TimeOfDay.TotalHours + longitude / 15 + equationMinutes / 60;
        var hourAngle = Radians(15 * (solarHours - 12));
        var phi = Radians(latitude);

        var up = Math.Sin(phi) * Math.Sin(declination) + Math.Cos(phi) * Math.Cos(declination) * Math.Cos(hourAngle);
        if (up <= 0.01)
            return 0;
        var east = -Math.Cos(declination) * Math.Sin(hourAngle);
        var north = Math.Cos(phi) * Math.Sin(declination) - Math.Sin(phi) * Math.Cos(declination) * Math.Cos(hourAngle);
        var intensity = Math.Pow(0.7, Math.Pow(1 / up, 0.678));

        return forecast.Planes.Sum(plane =>
        {
            var tilt = Radians(plane.TiltDegrees);
            var azimuth = Radians(plane.AzimuthDegrees);
            var incidence = -Math.Sin(tilt) * Math.Sin(azimuth) * east - Math.Sin(tilt) * Math.Cos(azimuth) * north + Math.Cos(tilt) * up;
            return plane.Kwp * Math.Max(incidence, 0) * intensity;
        });
    }

    private static double Radians(double degrees) => degrees * Math.PI / 180;
}
