namespace SKx.Solar.Deye;

public sealed record RegisterBlock(ushort Start, ushort[] Values)
{
    public bool Contains(int register) => register >= Start && register < Start + Values.Length;
    public ushort this[int register] => Values[register - Start];
}

/// <summary>Ein Messpunkt. Vorzeichen: Batterie negativ = laden, Netz positiv = Bezug.</summary>
public sealed record InverterSample
{
    public long Ts { get; init; }
    public int State { get; init; }
    public double PvW { get; init; }
    public double Pv1W { get; init; }
    public double Pv2W { get; init; }
    public double Pv1V { get; init; }
    public double Pv1A { get; init; }
    public double Pv2V { get; init; }
    public double Pv2A { get; init; }
    public double LoadW { get; init; }
    public double GridW { get; init; }
    public double BatteryW { get; init; }
    public double BatterySoc { get; init; }
    public double BatteryV { get; init; }
    public double BatteryA { get; init; }
    public double BatteryTempC { get; init; }
    public double InverterTempC { get; init; }
    public double DcTempC { get; init; }
    public double GridHz { get; init; }
    public double GridV1 { get; init; }
    public double GridV2 { get; init; }
    public double GridV3 { get; init; }
    public double GridW1 { get; init; }
    public double GridW2 { get; init; }
    public double GridW3 { get; init; }
    public double LoadW1 { get; init; }
    public double LoadW2 { get; init; }
    public double LoadW3 { get; init; }
    public double TodayPvKwh { get; init; }
    public double TodayLoadKwh { get; init; }
    public double TodayImportKwh { get; init; }
    public double TodayExportKwh { get; init; }
    public double TodayChargeKwh { get; init; }
    public double TodayDischargeKwh { get; init; }
    public double TotalPvKwh { get; init; }
    public double TotalLoadKwh { get; init; }
    public double TotalImportKwh { get; init; }
    public double TotalExportKwh { get; init; }
    public double TotalChargeKwh { get; init; }
    public double TotalDischargeKwh { get; init; }
    /// <summary>Roh-Fehlercodes des Geräts als Hex, leer ohne Fehler. Wird nicht in Samples gespeichert.</summary>
    public string FaultCodes { get; init; } = "";
}

/// <summary>
/// Hält unplausible Zählerstände zurück. Passen viele Messpunkte in Folge zueinander, aber nicht zum alten Stand,
/// war der alte Stand falsch oder das Gerät wurde getauscht. Dann gilt der neue, sonst käme nie wieder etwas an.
/// </summary>
public sealed class CounterGuard(InverterSample? previous)
{
    public const int SamplesUntilNewBaseline = 30;
    private InverterSample? _previous = previous;
    private InverterSample? _rejected;
    private int _rejectedCount;

    /// <summary>True, wenn der Messpunkt gilt. <paramref name="rebased"/> meldet den Wechsel auf einen neuen Stand.</summary>
    public bool Accept(InverterSample sample, out bool rebased)
    {
        rebased = false;
        if (!DeyeDecoder.InRange(sample))
            return false;
        if (!DeyeDecoder.IsPlausible(sample, _previous))
        {
            _rejectedCount = _rejected is not null && DeyeDecoder.IsPlausible(sample, _rejected) ? _rejectedCount + 1 : 1;
            _rejected = sample;
            if (_rejectedCount < SamplesUntilNewBaseline)
                return false;
            rebased = true;
        }
        _previous = sample;
        _rejected = null;
        _rejectedCount = 0;
        return true;
    }
}

/// <summary>
/// Registerbelegung der dreiphasigen Deye-Hybrid-Wechselrichter, abgeglichen mit dem Community-Projekt
/// ha-solarman (deye_p3) und mit Messwerten des eigenen Geräts (Gerätetyp 6, Hochvolt-Batterie).
/// </summary>
public static class DeyeDecoder
{
    public static readonly IReadOnlyList<(ushort Start, ushort Count)> Blocks =
    [
        (500, 42),
        (553, 6),
        (586, 40),
        (644, 12),
        (672, 12),
    ];

    public static InverterSample Decode(RegisterBlock[] blocks, long ts, bool highVoltage)
    {
        ushort U(int r) => blocks.First(b => b.Contains(r))[r];
        short S(int r) => unchecked((short)U(r));
        double U32(int low) => U(low) | ((uint)U(low + 1) << 16);
        double Temp(int r) => (S(r) - 1000) * 0.1;

        var powerScale = highVoltage ? 10.0 : 1.0;
        var batteryVoltScale = highVoltage ? 0.1 : 0.01;

        var pv1 = U(672) * powerScale;
        var pv2 = U(673) * powerScale;
        var pv3 = U(674) * powerScale;
        var pv4 = U(675) * powerScale;

        return new InverterSample
        {
            Ts = ts,
            State = U(500),
            PvW = pv1 + pv2 + pv3 + pv4,
            Pv1W = pv1,
            Pv2W = pv2,
            Pv1V = U(676) * 0.1,
            Pv1A = U(677) * 0.1,
            Pv2V = U(678) * 0.1,
            Pv2A = U(679) * 0.1,
            LoadW = S(653),
            GridW = S(625),
            BatteryW = S(590) * powerScale,
            BatterySoc = U(588),
            BatteryV = U(587) * batteryVoltScale,
            BatteryA = S(591) * 0.01,
            BatteryTempC = Temp(586),
            InverterTempC = Temp(541),
            DcTempC = Temp(540),
            GridHz = U(609) * 0.01,
            GridV1 = U(598) * 0.1,
            GridV2 = U(599) * 0.1,
            GridV3 = U(600) * 0.1,
            GridW1 = S(622),
            GridW2 = S(623),
            GridW3 = S(624),
            // Die Phasen können negativ werden, wenn der Wechselrichter über eine Phase zurückspeist.
            LoadW1 = S(650),
            LoadW2 = S(651),
            LoadW3 = S(652),
            TodayChargeKwh = U(514) * 0.1,
            TodayDischargeKwh = U(515) * 0.1,
            TodayImportKwh = U(520) * 0.1,
            TodayExportKwh = U(521) * 0.1,
            TodayLoadKwh = U(526) * 0.1,
            TodayPvKwh = U(529) * 0.1,
            TotalChargeKwh = U32(516) * 0.1,
            TotalDischargeKwh = U32(518) * 0.1,
            TotalImportKwh = U32(522) * 0.1,
            TotalExportKwh = U32(524) * 0.1,
            TotalLoadKwh = U32(527) * 0.1,
            TotalPvKwh = U32(534) * 0.1,
            FaultCodes = FaultText(U),
        };
    }

    private static string FaultText(Func<int, ushort> register)
    {
        var alarm = string.Concat(Enumerable.Range(553, 2).Select(r => register(r).ToString("X4")));
        var fault = string.Concat(Enumerable.Range(555, 4).Select(r => register(r).ToString("X4")));
        var parts = new List<string>();
        if (alarm.Any(c => c != '0')) parts.Add($"Alarm 0x{alarm}");
        if (fault.Any(c => c != '0')) parts.Add($"Fehler 0x{fault}");
        return string.Join(", ", parts);
    }

    /// <summary>Verwirft Messpunkte, die nur aus einer gestörten Übertragung stammen können.</summary>
    public static bool IsPlausible(InverterSample sample, InverterSample? previous)
    {
        if (!InRange(sample))
            return false;
        if (previous is null)
            return true;

        // Zählerstände laufen nur vorwärts, und ein Sprung um mehr als 500 kWh ist kein echter Ertrag.
        (double Now, double Before)[] counters =
        [
            (sample.TotalPvKwh, previous.TotalPvKwh),
            (sample.TotalLoadKwh, previous.TotalLoadKwh),
            (sample.TotalImportKwh, previous.TotalImportKwh),
            (sample.TotalExportKwh, previous.TotalExportKwh),
            (sample.TotalChargeKwh, previous.TotalChargeKwh),
            (sample.TotalDischargeKwh, previous.TotalDischargeKwh),
        ];
        return counters.All(c => c.Now >= c.Before && c.Now - c.Before <= 500);
    }

    public static bool InRange(InverterSample sample) =>
        sample.BatterySoc <= 100 && sample.GridHz <= 70 && sample.State <= 4
        && sample.PvW <= 100_000 && Math.Abs(sample.GridW) <= 30_000 && sample.LoadW <= 30_000;

    public static string StateName(int state) => state switch
    {
        0 => "Standby",
        1 => "Selbsttest",
        2 => "Normalbetrieb",
        3 => "Alarm",
        4 => "Störung",
        _ => "Unbekannt",
    };
}
