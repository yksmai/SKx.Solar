using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

public sealed record BatteryOutlook(long? FullTs, long? EmptyTs, bool AtReserve, double ReservePercent);
public sealed record SurplusWindow(long StartTs, long EndTs, double AvgW, bool BatteryFull);
public sealed record AccuracyDay(string Date, double ForecastKwh, double PvKwh);
public sealed record AccuracyResult(IReadOnlyList<AccuracyDay> Days, double? DeviationPct, double? SuggestedRatio, int UsableDays);

/// <summary>Rechnet aus Prognose, Verbrauchsprofil und Ladestand. Die App empfiehlt nur und schaltet nichts.</summary>
public static class Advisor
{
    private const int Horizon = 48;
    private const double FullPercent = 98;
    private const double MinSurplusW = 1000;
    private const double Efficiency = 0.95;

    /// <summary>Ein Schritt je Stunde: Überschuss lädt die Batterie, Mangel entlädt sie bis zur Reserve.</summary>
    public static (BatteryOutlook Outlook, IReadOnlyDictionary<long, double> Soc) Simulate(
        long now, double socPercent, double capacityKwh, double reservePercent,
        Func<long, double> loadW, IReadOnlyDictionary<long, double> pvW)
    {
        var soc = socPercent;
        var at = now;
        long? full = socPercent >= FullPercent ? now : null;
        long? empty = null;
        var atReserve = socPercent <= reservePercent;
        var byHour = new Dictionary<long, double>();
        for (var i = 0; i < Horizon; i++)
        {
            var hour = at - at % 3600;
            byHour[hour] = soc;
            var seconds = hour + 3600 - at;
            var netW = pvW.GetValueOrDefault(hour) - loadW(hour);
            var percentPerSecond = (netW > 0 ? netW * Efficiency : netW / Efficiency) / 1000 / capacityKwh * 100 / 3600;
            var next = soc + percentPerSecond * seconds;
            if (full is null && next >= FullPercent && percentPerSecond > 0)
                full = at + (long)((FullPercent - soc) / percentPerSecond);
            if (empty is null && !atReserve && next <= reservePercent && percentPerSecond < 0)
                empty = at + (long)((reservePercent - soc) / percentPerSecond);
            soc = Math.Clamp(next, reservePercent, 100);
            at = hour + 3600;
        }
        return (new(full, empty, atReserve, reservePercent), byHour);
    }

    /// <summary>Das energiereichste zusammenhängende Fenster mit Überschuss, heute zuerst, sonst am nächsten Tag.</summary>
    public static SurplusWindow? BestWindow(long now, Func<long, double> loadW, IReadOnlyDictionary<long, double> pvW,
        IReadOnlyDictionary<long, double> soc, Func<long, string> localDate)
    {
        var hours = pvW.Keys.Where(h => h + 3600 > now).Order().ToList();
        foreach (var day in hours.Select(localDate).Distinct().Take(2))
        {
            var windows = new List<List<long>>();
            List<long>? current = null;
            foreach (var hour in hours.Where(h => localDate(h) == day))
            {
                if (pvW[hour] - loadW(hour) >= MinSurplusW)
                {
                    if (current is null || current[^1] + 3600 != hour)
                        windows.Add(current = []);
                    current.Add(hour);
                }
                else
                {
                    current = null;
                }
            }
            var best = windows.MaxBy(w => w.Sum(h => pvW[h] - loadW(h)));
            if (best is not null)
                return new(best[0], best[^1] + 3600, Math.Round(best.Average(h => pvW[h] - loadW(h))),
                    soc.GetValueOrDefault(best[0]) >= FullPercent);
        }
        return null;
    }

    /// <summary>
    /// Vergleicht Prognose und Ertrag. Tage mit voller Batterie zählen nicht für den Vorschlag,
    /// weil die Anlage dort abregeln kann und der Ertrag dann nichts über die Dachleistung sagt.
    /// </summary>
    public static AccuracyResult Accuracy(IReadOnlyList<ForecastActual> rows, double performanceRatio)
    {
        var days = rows.Select(r => new AccuracyDay(r.Date, Math.Round(r.NormKwh * performanceRatio, 1), r.PvKwh)).ToList();
        var forecast = days.Sum(d => d.ForecastKwh);
        var usable = rows.Where(r => r.NormKwh >= 1 && r.MaxSoc is < FullPercent).Select(r => r.PvKwh / r.NormKwh).Order().ToList();
        double? suggestion = usable.Count >= 7 ? Math.Round(Math.Clamp(usable[usable.Count / 2], 0.4, 1), 2) : null;
        return new(days, forecast > 0 ? Math.Round((days.Sum(d => d.PvKwh) / forecast - 1) * 100) : null, suggestion, usable.Count);
    }
}
