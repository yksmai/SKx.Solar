using Dapper;
using Microsoft.Data.Sqlite;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Storage;
using Xunit;

namespace SKx.Solar.Tests;

public class SolarmanFrameTests
{
    [Fact]
    public void BuildReadRequest_UsesOnlyModbusReadFunction()
    {
        var frame = SolarmanFrame.BuildReadRequest(1234567890, 1, 500, 42);

        Assert.Equal(0xA5, frame[0]);
        Assert.Equal(0x15, frame[^1]);
        Assert.Equal(3, frame[11 + 15 + 1]);
        Assert.Equal(frame.Skip(1).Take(frame.Length - 3).Aggregate(0, (a, b) => (a + b) & 0xFF), frame[^2]);
    }

    [Fact]
    public void ParseReadResponse_ReturnsValues()
    {
        Assert.Equal(new ushort[] { 1250, 3125 }, SolarmanFrame.ParseReadResponse(Response([1250, 3125]), 2));
    }

    [Fact]
    public void ParseReadResponse_RejectsCorruptCrc()
    {
        var frame = Response([1250]);
        frame[11 + 14 + 3] ^= 0xFF;
        frame[^2] = (byte)frame.Skip(1).Take(frame.Length - 3).Aggregate(0, (a, b) => (a + b) & 0xFF);

        Assert.Throws<InvalidDataException>(() => SolarmanFrame.ParseReadResponse(frame, 1));
    }

    [Fact]
    public void ParseReadResponse_RejectsWrongCount()
    {
        Assert.Throws<InvalidDataException>(() => SolarmanFrame.ParseReadResponse(Response([1, 2]), 3));
    }

    private static byte[] Response(ushort[] values)
    {
        var rtu = new List<byte> { 1, 3, (byte)(values.Length * 2) };
        foreach (var v in values) { rtu.Add((byte)(v >> 8)); rtu.Add((byte)v); }
        var crc = SolarmanFrame.Crc(rtu.ToArray());
        rtu.Add((byte)crc);
        rtu.Add((byte)(crc >> 8));

        var payloadLength = 14 + rtu.Count;
        var frame = new List<byte> { 0xA5, (byte)payloadLength, (byte)(payloadLength >> 8), 0x10, 0x15, 0, 0, 0, 0, 0, 0 };
        frame.AddRange(new byte[14]);
        frame.AddRange(rtu);
        frame.Add((byte)frame.Skip(1).Aggregate(0, (a, b) => (a + b) & 0xFF));
        frame.Add(0x15);
        return frame.ToArray();
    }
}

public class DeyeDecoderTests
{
    // Echte Werte des Geräts vom 31.10.2026.
    private static RegisterBlock[] Captured()
    {
        var a = new ushort[42];
        a[0] = 2; a[14] = 12; a[16] = 240; a[18] = 273; a[20] = 54; a[22] = 257; a[26] = 56; a[27] = 491; a[29] = 28; a[34] = 302; a[40] = 1250; a[41] = 1366;
        var b = new ushort[40];
        b[0] = 1250; b[1] = 3126; b[2] = 21; b[4] = unchecked((ushort)-66); b[5] = unchecked((ushort)-214);
        b[12] = 2263; b[13] = 2313; b[14] = 2309; b[23] = 5000; b[36] = 3; b[37] = 11; b[38] = 2; b[39] = 16;
        var c = new ushort[12];
        c[6] = 50; c[7] = 425; c[8] = 28; c[9] = 503;
        var d = new ushort[12];
        d[0] = 69; d[1] = 61; d[4] = 4263; d[5] = 15; d[6] = 3617; d[7] = 17;
        return [new(500, a), new(553, new ushort[6]), new(586, b), new(644, c), new(672, d)];
    }

    [Fact]
    public void Decode_ReportsFaultCodesAsHex()
    {
        var blocks = Captured();
        Assert.Equal("", DeyeDecoder.Decode(blocks, 100, true).FaultCodes);

        blocks[1].Values[0] = 0x0012;
        blocks[1].Values[5] = 0x8000;

        Assert.Equal("Alarm 0x00120000, Fehler 0x0000000000008000", DeyeDecoder.Decode(blocks, 100, true).FaultCodes);
    }

    [Fact]
    public void Decode_MatchesCapturedDevice()
    {
        var s = DeyeDecoder.Decode(Captured(), 100, highVoltage: true);

        Assert.Equal(1300, s.PvW, 1);
        Assert.Equal(-660, s.BatteryW, 1);
        Assert.Equal(21, s.BatterySoc);
        Assert.Equal(312.6, s.BatteryV, 1);
        Assert.Equal(-2.14, s.BatteryA, 2);
        Assert.Equal(25.0, s.BatteryTempC, 1);
        Assert.Equal(36.6, s.InverterTempC, 1);
        Assert.Equal(16, s.GridW, 1);
        Assert.Equal(503, s.LoadW, 1);
        Assert.Equal(50.0, s.GridHz, 2);
        Assert.Equal(2.8, s.TodayPvKwh, 1);
        Assert.Equal(30.2, s.TotalPvKwh, 1);
        Assert.Equal(49.1, s.TotalLoadKwh, 1);
    }

    [Fact]
    public void Decode_ReadsPhaseLoadAsSigned()
    {
        var blocks = Captured();
        blocks[3].Values[8] = 65451;

        Assert.Equal(-85, DeyeDecoder.Decode(blocks, 100, true).LoadW3, 1);
    }

    [Fact]
    public void Decode_LowVoltageUsesOtherScales()
    {
        var s = DeyeDecoder.Decode(Captured(), 100, highVoltage: false);

        Assert.Equal(130, s.PvW, 1);
        Assert.Equal(31.26, s.BatteryV, 2);
    }

    [Fact]
    public void IsPlausible_RejectsImpossibleValues()
    {
        var good = DeyeDecoder.Decode(Captured(), 100, true);

        Assert.True(DeyeDecoder.IsPlausible(good, null));
        Assert.False(DeyeDecoder.IsPlausible(good with { BatterySoc = 300 }, null));
        Assert.False(DeyeDecoder.IsPlausible(good with { TotalPvKwh = good.TotalPvKwh - 1 }, good));
        Assert.False(DeyeDecoder.IsPlausible(good with { TotalPvKwh = good.TotalPvKwh + 5000 }, good));
    }

    [Fact]
    public void ParseDevice_ReadsSerialAndVoltageClass()
    {
        var device = CollectorService.ParseDevice(new RegisterBlock(0, [6, 1, 260, 12852, 12343, 12851, 13362, 14132]));

        Assert.Equal("2407234274", device.SerialNumber);
        Assert.True(device.HighVoltage);
    }
}

public sealed class SolarDbTests : IDisposable
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
    private readonly string _dir = Directory.CreateTempSubdirectory("skx-solar").FullName;
    private readonly SolarDb _db;

    public SolarDbTests()
    {
        _db = new SolarDb(Path.Combine(_dir, "solar.db"));
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    private static long Ts(int year, int month, int day, int hour, int minute, int second = 0) =>
        new DateTimeOffset(new DateTime(year, month, day, hour, minute, second), Berlin.GetUtcOffset(new DateTime(year, month, day, hour, minute, second))).ToUnixTimeSeconds();

    private static InverterSample Sample(long ts, double pv = 1000, double totalPv = 100, double todayPv = 2, double totalLoad = 200, double todayLoad = 4, double grid = 0, double battery = 0) => new()
    {
        Ts = ts,
        PvW = pv,
        LoadW = 500,
        GridW = grid,
        BatteryW = battery,
        BatterySoc = 50,
        TotalPvKwh = totalPv,
        TodayPvKwh = todayPv,
        TotalLoadKwh = totalLoad,
        TodayLoadKwh = todayLoad,
    };

    [Fact]
    public void FirstDay_UsesInverterTodayCounterAsStart()
    {
        _db.Store([Sample(Ts(2026, 6, 1, 12, 0))], Berlin);

        var day = _db.Day("2026-06-01")!;
        Assert.Equal(2, day.PvKwh, 1);
        Assert.Equal(4, day.LoadKwh, 1);
    }

    [Fact]
    public void NextDay_StartsAtPreviousEnd_SoOutageEnergyIsKept()
    {
        _db.Store([Sample(Ts(2026, 6, 1, 12, 0), totalPv: 100)], Berlin);
        _db.Store([Sample(Ts(2026, 6, 3, 12, 0), totalPv: 130, todayPv: 5)], Berlin);

        Assert.Equal(30, _db.Day("2026-06-03")!.PvKwh, 1);
    }

    [Fact]
    public void Store_SplitsAtLocalMidnight()
    {
        _db.Store(
        [
            Sample(Ts(2026, 6, 1, 23, 59), totalPv: 110),
            Sample(Ts(2026, 6, 2, 0, 1), totalPv: 110.5),
        ], Berlin);

        Assert.NotNull(_db.Day("2026-06-01"));
        Assert.Equal(0.5, _db.Day("2026-06-02")!.PvKwh, 1);
    }

    [Fact]
    public void Minutes_SeparateImportAndExport()
    {
        var ts = Ts(2026, 6, 1, 12, 0);
        _db.Store([Sample(ts, grid: 100), Sample(ts + 10, grid: -300)], Berlin);

        var minute = Assert.Single(_db.Minutes("2026-06-01"));
        Assert.Equal(50, minute.ImportW, 1);
        Assert.Equal(150, minute.ExportW, 1);
        Assert.Equal("2026-06-01 12:00", minute.LocalTime);
        Assert.Equal(2, minute.SampleCount);
    }

    [Fact]
    public void Daily_ComputesAutarky()
    {
        _db.Store([Sample(Ts(2026, 6, 1, 12, 0)) with { TotalLoadKwh = 10, TodayLoadKwh = 10, TotalImportKwh = 2, TodayImportKwh = 2 }], Berlin);

        Assert.Equal(80, _db.Day("2026-06-01")!.AutarkyPct);
    }

    [Fact]
    public void Retention_RemovesOnlyRawSamples()
    {
        var ts = Ts(2026, 6, 1, 12, 0);
        _db.Store([Sample(ts)], Berlin);

        Assert.Equal(1, _db.DeleteSamplesBefore(ts + 1));
        Assert.Single(_db.Minutes("2026-06-01"));
        Assert.NotNull(_db.Day("2026-06-01"));
    }

    [Fact]
    public void Csv_UsesGermanFormat()
    {
        _db.Store([Sample(Ts(2026, 6, 1, 12, 0), todayPv: 2.5)], Berlin);
        using var output = new MemoryStream();

        _db.WriteCsv(output, "v_SolarDaily", "Date", "2026-06-01", "2026-06-01");

        var lines = System.Text.Encoding.UTF8.GetString(output.ToArray()).TrimStart('﻿').Split(Environment.NewLine);
        Assert.StartsWith("Date;PvKwh;", lines[0]);
        Assert.StartsWith("2026-06-01;2,5;", lines[1]);
    }

    [Fact]
    public void Csv_RejectsUnknownView()
    {
        Assert.Throws<ArgumentException>(() => _db.WriteCsv(Stream.Null, "Samples; DROP TABLE Days", "Date", "a", "b"));
    }

    [Fact]
    public void Snapshot_ContainsAnalysisViews()
    {
        _db.Store([Sample(Ts(2026, 6, 1, 12, 0))], Berlin);

        var path = _db.CreateSnapshot();

        using var copy = new SqliteConnection($"Data Source={path}");
        copy.Open();
        Assert.Equal(1, copy.ExecuteScalar<int>("SELECT COUNT(*) FROM v_SolarMinute"));
        Assert.Equal(1, copy.ExecuteScalar<int>("SELECT COUNT(*) FROM v_SolarDaily"));
        Assert.Equal(1, copy.ExecuteScalar<int>("SELECT COUNT(*) FROM v_SolarMonthly"));
    }
}
