using SKx.Solar.Storage;

namespace SKx.Solar.Integrations;

/// <summary>Minutenweise aufsummierte Stromwege eines Tages in kWh, nur für die Verhältnisse.</summary>
public sealed class MinuteFlows
{
    public double Import { get; set; }
    public double Discharge { get; set; }
    public double Charge { get; set; }
    public double GridHome { get; set; }
    public double BatteryHome { get; set; }
    public double GridBattery { get; set; }
}

/// <param name="DirectKwh">Solarstrom, der direkt im Haus verbraucht wurde.</param>
/// <param name="SolarOwnUseKwh">Solarstrom für den Eigenverbrauch des Wechselrichters.</param>
public sealed record DayFlows(double DirectKwh, double BatteryHomeKwh, double GridHomeKwh, double SolarChargeKwh, double SolarOwnUseKwh);

/// <summary>
/// Der Wechselrichter braucht selbst Strom, den kein Zähler als Verbrauch führt. Die Tagessummen allein sagen nicht,
/// woher er kam: Nachts aus dem Netz, abends aus der Batterie, tagsüber vom Dach. Das verraten erst die Minutenwerte.
/// Die Mengen bleiben die der genaueren Zähler, von den Minuten stammen nur die Anteile.
/// </summary>
public static class EnergyFlows
{
    public static DayFlows Compute(DayEnergy day, MinuteFlows? minutes)
    {
        var gridHome = day.ImportKwh * Share(minutes?.GridHome, minutes?.Import);
        var batteryHome = day.DischargeKwh * Share(minutes?.BatteryHome, minutes?.Discharge);
        var gridBattery = minutes is { Import: > 0 } ? day.ImportKwh * minutes.GridBattery / minutes.Import : 0;
        var direct = Math.Max(day.LoadKwh - gridHome - batteryHome, 0);
        var solarCharge = Math.Max(day.ChargeKwh - gridBattery, 0);
        var ownUse = Math.Max(day.PvKwh - solarCharge - day.ExportKwh - direct, 0);
        return new(Round(direct), Round(batteryHome), Round(gridHome), Round(solarCharge), Round(ownUse));
    }

    /// <summary>Ohne Minutenwerte zählt alles als ans Haus geliefert.</summary>
    private static double Share(double? part, double? total) => total is > 0 ? Math.Min(part!.Value / total.Value, 1) : 1;

    private static double Round(double value) => Math.Round(value, 2);
}
