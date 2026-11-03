using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using MQTTnet.Server;
using SKx.Solar.Alerts;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Integrations;
using SKx.Solar.Security;
using SKx.Solar.Storage;
using Xunit;

namespace SKx.Solar.Tests;

public class RoofCheckTests
{
    private static readonly long Start = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static ForecastOptions Options(double azimuth) => new()
    {
        Latitude = 51,
        Longitude = 10,
        Planes = [new PvPlane { Kwp = 5, TiltDegrees = 35, AzimuthDegrees = azimuth }],
    };

    /// <summary>Klare Tage aus demselben Modell, nur mit der tatsächlichen Ausrichtung.</summary>
    private static List<RoofMinute> ClearDays(int days, double actualAzimuth, Func<long, double>? factor = null)
    {
        var real = Options(actualAzimuth);
        var minutes = new List<RoofMinute>();
        for (var day = 0; day < days; day++)
        {
            for (var minute = 0; minute < 1440; minute++)
            {
                var ts = Start + day * 86400L + minute * 60L;
                var power = RoofCheck.Power(ts, real) * 1000 * (factor?.Invoke(ts) ?? 1);
                if (power > 20)
                    minutes.Add(new RoofMinute { Ts = ts, LocalDate = $"2026-09-{day + 1:00}", PvW = power, BatterySoc = 60 });
            }
        }
        return minutes;
    }

    [Fact]
    public void Matching_orientation_is_confirmed()
    {
        var result = RoofCheck.Evaluate(ClearDays(4, 0), Options(0));
        Assert.Equal("ok", result.Verdict);
        Assert.Equal(4, result.ClearDays);
    }

    [Theory]
    [InlineData(60, "later")]
    [InlineData(-60, "earlier")]
    public void Wrong_orientation_points_to_the_real_direction(double actual, string verdict)
    {
        var result = RoofCheck.Evaluate(ClearDays(4, actual), Options(0));
        Assert.Equal(verdict, result.Verdict);
    }

    [Fact]
    public void Cloudy_days_and_too_few_days_give_no_verdict()
    {
        var cloudy = ClearDays(5, 0, ts => ts / 1800 % 2 == 0 ? 1 : 0.2);
        Assert.Equal(new RoofCheckResult(true, "waiting", 0, RoofCheck.NeededDays, null), RoofCheck.Evaluate(cloudy, Options(0)));
        Assert.Equal("waiting", RoofCheck.Evaluate(ClearDays(2, 0), Options(0)).Verdict);
    }

    [Fact]
    public void Days_with_a_full_battery_do_not_count()
    {
        var full = ClearDays(4, 0);
        full.ForEach(m => m.BatterySoc = 100);
        Assert.Equal(0, RoofCheck.Evaluate(full, Options(0)).ClearDays);
    }

    [Fact]
    public void Without_planes_nothing_is_checked() =>
        Assert.False(RoofCheck.Evaluate([], new ForecastOptions()).Configured);
}

public class BalconyTests : IDisposable
{
    private static readonly long Noon = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"balcony-{Guid.NewGuid():N}.db");
    private readonly SolarDb _db;

    public BalconyTests()
    {
        _db = new SolarDb(_path);
        _db.Initialize();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            File.Delete(_path + suffix);
    }

    private static InverterSample Sample(long ts, double pv, double load, double import, double export) => new()
    {
        Ts = ts, PvW = 1000, LoadW = 400, TotalPvKwh = pv, TodayPvKwh = pv, TotalLoadKwh = load, TodayLoadKwh = load,
        TotalImportKwh = import, TodayImportKwh = import, TotalExportKwh = export, TodayExportKwh = export,
    };

    [Fact]
    public void Parse_AddsBothInputs()
    {
        var point = BalconyParser.Parse("""{"data":{"p1":120,"e1":0.5,"te1":40.5,"p2":80,"e2":0.25,"te2":30},"message":"SUCCESS","deviceId":"E17"}""", 5);
        Assert.Equal(new BalconyPoint(5, 200, 0.75, 70.5), point);
    }

    [Fact]
    public void Parse_RejectsDeviceError() =>
        Assert.Throws<InvalidDataException>(() => BalconyParser.Parse("""{"data":{},"message":"FAILED"}""", 5));

    [Fact]
    public void Balance_CountsBalconyAsGenerationAndLoad_GridStaysMeasured()
    {
        _db.Store([Sample(Noon, 10, 8, 4, 1)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon, 300, 2, 102)], TimeZoneInfo.Utc);

        var day = _db.Day("2026-06-02")!;
        Assert.Equal(2, day.BalconyKwh);
        Assert.Equal(12, day.PvKwh);
        Assert.Equal(10, day.LoadKwh);
        Assert.Equal(4, day.ImportKwh);
        Assert.Equal(1, day.ExportKwh);
        // Die Testwerte lassen 5 kWh offen, die zählen als Eigenverbrauch des Wechselrichters zum Bedarf.
        Assert.Equal(5, day.OwnUseKwh);
        Assert.Equal(73.3, day.AutarkyPct);
        Assert.Equal(1300, day.PeakPvW);
        Assert.Equal(300, _db.Minutes("2026-06-02").Single().BalconyW);
    }

    [Fact]
    public void Minutes_MixOfWithAndWithoutBalcony_CanBeRead()
    {
        _db.Store([Sample(Noon, 1, 1, 0, 0), Sample(Noon + 60, 1, 1, 0, 0), Sample(Noon + 120, 1, 1, 0, 0)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon + 60, 26, 0, 10)], TimeZoneInfo.Utc);

        Assert.Equal([0, 26, 0], _db.Minutes("2026-06-02").Select(m => m.BalconyW));
    }

    [Fact]
    public void NextDay_StartsAtLastCounter_AndDeviceSwapIsNotNegative()
    {
        _db.Store([Sample(Noon, 1, 1, 0, 0), Sample(Noon + 86400, 2, 2, 0, 0), Sample(Noon + 2 * 86400, 3, 3, 0, 0)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon, 100, 1, 50)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon + 86400, 100, 3, 53)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon + 2 * 86400, 100, 1, 1)], TimeZoneInfo.Utc);

        Assert.Equal(3, _db.Day("2026-06-03")!.BalconyKwh);
        Assert.Equal(0, _db.Day("2026-06-04")!.BalconyKwh);
    }

    [Fact]
    public void ForecastComparison_LeavesBalconyOut()
    {
        _db.Store([Sample(Noon, 10, 8, 0, 0)], TimeZoneInfo.Utc);
        _db.StoreBalcony([new(Noon, 300, 2, 102)], TimeZoneInfo.Utc);
        _db.SaveForecast("2026-06-02", [("2026-06-02", 20)]);

        Assert.Equal(10, _db.ForecastActuals("2026-06-01", "2026-06-03").Single().PvKwh);
    }

    [Fact]
    public void State_TreatsOldReadingAsNoGeneration()
    {
        var state = new BalconyState { Latest = new(Noon, 300, 1, 1) };
        Assert.Equal(300, state.W(Noon + 60));
        Assert.Equal(0, state.W(Noon + 600));
    }

    [Theory]
    [InlineData("192.168.1.27", 8050, true)]
    [InlineData("apsystems.fritz.box", 8050, true)]
    [InlineData(null, 8050, true)]
    [InlineData("a/b?c", 8050, false)]
    [InlineData("192.168.1.27", 0, false)]
    public void Settings_ValidateAddress(string? host, int port, bool valid)
    {
        var settings = new PlanningSettings { Balcony = new() { Host = host, Port = port } };
        Assert.Equal(valid, settings.Validate() is null);
    }
}

public class AlertRuleTests
{
    private const long Now = 100_000;
    private static readonly IReadOnlySet<string> None = new HashSet<string>();

    private static InverterSample Healthy => new()
    {
        Ts = Now - 5, State = 2, BatterySoc = 50, InverterTempC = 40, Pv1V = 400, Pv1A = 2, Pv2V = 400, Pv2A = 2,
    };

    private static AlertInput Input(InverterSample? sample, int hour = 12, double? median = null, double? today = null, string? backup = null) =>
        new(sample, Now, Now - 10_000, hour, 15, 75, median, today, backup);

    private static List<string> Keys(AlertInput input, IReadOnlySet<string>? open = null) =>
        AlertRules.Evaluate(input, open ?? None).Select(c => c.Key).ToList();

    [Fact]
    public void Healthy_RaisesNothing() => Assert.Empty(Keys(Input(Healthy)));

    [Fact]
    public void OldSample_RaisesNoData_AndSuppressesStaleRules()
    {
        var keys = Keys(Input(Healthy with { Ts = Now - 301, BatterySoc = 1 }));
        Assert.Equal(["no_data"], keys);
    }

    [Fact]
    public void ShortlyAfterStart_NoSampleIsNoAlert()
    {
        Assert.Empty(AlertRules.Evaluate(new AlertInput(null, Now, Now - 60, 12, 15, 75, null, null, null), None));
    }

    [Theory]
    [InlineData(3, "warning")]
    [InlineData(4, "critical")]
    public void FaultState_RaisesFault(int state, string severity)
    {
        var condition = AlertRules.Evaluate(Input(Healthy with { State = state }), None).Single();
        Assert.Equal("inverter_fault", condition.Key);
        Assert.Equal(severity, condition.Severity);
    }

    [Fact]
    public void FaultCodes_AreCarriedIntoTheMessage()
    {
        var condition = AlertRules.Evaluate(Input(Healthy with { FaultCodes = "Fehler 0x01" }), None).Single();
        Assert.Equal("Fehler 0x01", condition.Message);
    }

    [Fact]
    public void BatteryLow_HasHysteresis()
    {
        Assert.Contains("battery_low", Keys(Input(Healthy with { BatterySoc = 15 })));
        Assert.Empty(Keys(Input(Healthy with { BatterySoc = 18 })));
        Assert.Contains("battery_low", Keys(Input(Healthy with { BatterySoc = 18 }), new HashSet<string> { "battery_low" }));
        Assert.Empty(Keys(Input(Healthy with { BatterySoc = 20 }), new HashSet<string> { "battery_low" }));
    }

    [Fact]
    public void IdleString_NeedsHoldTime_AndOtherStringWorking()
    {
        var idle = AlertRules.Evaluate(Input(Healthy with { Pv1A = 0, Pv1V = 485 }), None).Single();
        Assert.Equal("string_idle", idle.Key);
        Assert.Equal(900, idle.HoldSeconds);

        // Nachts haben beide keinen Strom, das ist kein Fehler.
        Assert.Empty(Keys(Input(Healthy with { Pv1A = 0, Pv2A = 0 })));
    }

    [Fact]
    public void PvLow_OnlyInTheEvening()
    {
        Assert.Empty(Keys(Input(Healthy, hour: 12, median: 20, today: 1)));
        Assert.Contains("pv_low", Keys(Input(Healthy, hour: 18, median: 20, today: 1)));
        Assert.DoesNotContain("pv_low", Keys(Input(Healthy, hour: 18, median: 20, today: 10)));
    }

    [Fact]
    public void BackupError_RaisesAlert() => Assert.Contains("backup_failed", Keys(Input(Healthy, backup: "kein Platz")));

    [Fact]
    public void Tracker_WaitsForHoldTime_AndClosesGoneAlerts()
    {
        var tracker = new AlertTracker();
        var condition = new AlertCondition("x", Severity.Warning, "t", "m", HoldSeconds: 900);
        var open = new HashSet<string>();

        Assert.Empty(tracker.Step([condition], open, 1000).Opened);
        Assert.Empty(tracker.Step([condition], open, 1899).Opened);
        Assert.Single(tracker.Step([condition], open, 1900).Opened);

        open.Add("x");
        Assert.Empty(tracker.Step([condition], open, 2000).Closed);
        Assert.Equal(["x"], tracker.Step([], open, 2100).Closed);
    }

    [Fact]
    public void Tracker_RestartsHoldTime_WhenConditionInterrupted()
    {
        var tracker = new AlertTracker();
        var condition = new AlertCondition("x", Severity.Warning, "t", "m", HoldSeconds: 900);
        var open = new HashSet<string>();

        tracker.Step([condition], open, 1000);
        tracker.Step([], open, 1500);

        Assert.Empty(tracker.Step([condition], open, 1950).Opened);
    }
}

public class EconomicsTests
{
    private static readonly List<Tariff> Tariffs =
    [
        new() { From = "2026-01-01", ImportEurPerKwh = 0.30, FeedInEurPerKwh = 0.08 },
        new() { From = "2026-07-01", ImportEurPerKwh = 0.40, FeedInEurPerKwh = 0.10 },
    ];

    private static DayEnergy Day(string date) => new() { Date = date, LoadKwh = 10, ImportKwh = 2, ExportKwh = 5 };

    [Fact]
    public void Savings_UseTariffValidOnThatDay()
    {
        Assert.Equal(8 * 0.30 + 5 * 0.08, Economics.Savings(Day("2026-03-01"), Tariffs)!.Value, 5);
        Assert.Equal(8 * 0.40 + 5 * 0.10, Economics.Savings(Day("2026-07-01"), Tariffs)!.Value, 5);
    }

    [Fact]
    public void Savings_BeforeFirstTariff_AreUnknown() => Assert.Null(Economics.Savings(Day("2025-12-31"), Tariffs));

    [Fact]
    public void Sum_WithoutTariffs_IsNull() => Assert.Null(Economics.Sum([Day("2026-03-01")], []));

    [Fact]
    public void Payback_NeedsThirtyDays()
    {
        Assert.Null(Economics.PaybackYears(100, 29, 10_000));
        Assert.Equal(10, Economics.PaybackYears(1000, 365, 10_000));
    }
}

public class ReviewFixTests
{
    private static InverterSample Sample(double pv) => new() { TotalPvKwh = pv, GridHz = 50, State = 2 };

    [Fact]
    public void CounterGuard_TakesNewBaselineAfterManyConsistentSamples()
    {
        var guard = new CounterGuard(Sample(1000));
        for (var i = 0; i < CounterGuard.SamplesUntilNewBaseline - 1; i++)
            Assert.False(guard.Accept(Sample(10 + i), out _));

        Assert.True(guard.Accept(Sample(50), out var rebased));
        Assert.True(rebased);
        Assert.True(guard.Accept(Sample(51), out rebased));
        Assert.False(rebased);
    }

    [Fact]
    public void CounterGuard_ResetsWhenRejectedSamplesDisagree()
    {
        var guard = new CounterGuard(Sample(1000));
        for (var i = 0; i < 2 * CounterGuard.SamplesUntilNewBaseline; i++)
            Assert.False(guard.Accept(Sample(i % 2 == 0 ? 10 : 5), out _));
    }

    [Fact]
    public void NextCounters_KeepsDayEnergyWhenCounterRunsBackwards()
    {
        var existing = new DayCounters { Date = "2026-06-01", PvStart = 1000, PvEnd = 1012 };
        var next = Aggregation.NextCounters("2026-06-01", Sample(3), existing, null);
        Assert.Equal(12, next.PvEnd - next.PvStart);
        Assert.Equal(3, next.PvEnd);
    }

    [Fact]
    public void EnergyFlows_AssignsOwnUseToItsSource()
    {
        var day = new DayEnergy { PvKwh = 18.9, LoadKwh = 17.1, ImportKwh = 6.1, DischargeKwh = 3.7, ChargeKwh = 8.9, BalconyKwh = 1.2 };
        var flows = EnergyFlows.Compute(day, new MinuteFlows { Import = 6.1, GridHome = 5.1, Discharge = 3.7, BatteryHome = 3.3, Charge = 8.9 });

        Assert.Equal(5.1, flows.GridHomeKwh, 2);
        Assert.Equal(3.3, flows.BatteryHomeKwh, 2);
        Assert.Equal(8.7, flows.DirectKwh, 2);
        Assert.Equal(1.3, flows.SolarOwnUseKwh, 2);
        Assert.Equal(day.PvKwh, flows.DirectKwh + flows.SolarChargeKwh + flows.SolarOwnUseKwh, 2);
    }

    [Fact]
    public void EnergyFlows_FallsBackWithoutMinutes()
    {
        var flows = EnergyFlows.Compute(new DayEnergy { PvKwh = 5, LoadKwh = 4, ImportKwh = 1, ChargeKwh = 2 }, null);
        Assert.Equal(3, flows.DirectKwh, 2);
        Assert.Equal(1, flows.GridHomeKwh, 2);
    }

    [Fact]
    public void Tracker_ClosesUndervoltageOnlyAfterQuietPeriod()
    {
        var tracker = new AlertTracker();
        var open = new HashSet<string> { "pi_undervoltage" };
        var condition = new AlertCondition("pi_undervoltage", Severity.Warning, "t", "m");

        Assert.Empty(tracker.Step([], open, 1000).Closed);
        Assert.Empty(tracker.Step([condition], open, 1500).Closed);
        Assert.Empty(tracker.Step([], open, 2000).Closed);
        Assert.Equal(["pi_undervoltage"], tracker.Step([], open, 3800).Closed);
    }

    [Fact]
    public void Alerts_StayOpenDuringOutage()
    {
        var input = new AlertInput(Sample(1) with { Ts = 0 }, 1000, 0, 12, 15, 75, null, null, null);
        var keys = AlertRules.Evaluate(input, new HashSet<string> { "battery_low" }).Select(c => c.Key);
        Assert.Contains("battery_low", keys);
        Assert.Contains("no_data", keys);
    }

    [Fact]
    public void Sequence_MustMatchLowByte()
    {
        var frame = new byte[12];
        frame[5] = 0x34;
        Assert.True(SolarmanFrame.MatchesSequence(frame, 0x1234));
        Assert.False(SolarmanFrame.MatchesSequence(frame, 0x1235));
    }

    [Fact]
    public void Forecast_CountsValueForStartOfItsHour()
    {
        var hours = new SortedDictionary<long, double>();
        ForecastCalculator.AddPlane(hours, """{"hourly":{"time":[3600,7200,10800],"global_tilted_irradiance":[900,500,300]}}""", kwp: 1, performanceRatio: 1);

        Assert.False(hours.ContainsKey(0));
        Assert.Equal(500, hours[3600], 1);
        Assert.Equal(300, hours[7200], 1);
    }

    [Fact]
    public void Backup_CopiesPasswordToo()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "ziel");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(root, "auth.json"), "{}");
        try
        {
            SettingsStore.CopyTo(Path.Combine(root, "solar.db"), target);
            Assert.True(File.Exists(Path.Combine(target, "solar-auth.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public class ParserTests
{
    [Fact]
    public void Forecast_AddsPlanesAndSumsDays()
    {
        const string json = """{"hourly":{"time":[1000,4600,8200],"global_tilted_irradiance":[900,500,null]}}""";
        var hours = new SortedDictionary<long, double>();

        ForecastCalculator.AddPlane(hours, json, kwp: 5, performanceRatio: 0.8);
        ForecastCalculator.AddPlane(hours, json, kwp: 2, performanceRatio: 0.8);

        Assert.Equal(2800, hours[1000], 1);
        Assert.False(hours.ContainsKey(4600));
        Assert.Equal(2.8, ForecastCalculator.Build(hours, 0, TimeZoneInfo.Utc).Days.Single().Kwh, 1);
    }

    [Fact]
    public void Prices_ConvertToCentAndApplyExtras()
    {
        const string json = """{"data":[{"start_timestamp":3600000,"marketprice":100.0},{"start_timestamp":7200000,"marketprice":50.0}]}""";

        var plain = PriceCalculator.Parse(json, 0, 0);
        Assert.Equal(10, plain[0].Ct);
        Assert.Equal(3600, plain[0].T);
        Assert.Equal(23.8, PriceCalculator.Parse(json, 10, 19)[0].Ct, 2);
        Assert.Equal([7200L], PriceCalculator.CheapestHours(plain, 0, 1));
    }

    [Fact]
    public void Payload_ComputesSurplusFromExportAndCharging()
    {
        using var doc = JsonDocument.Parse(MqttPayload.Build(new InverterSample { GridW = -300, BatteryW = -500, PvW = 1000 }));
        Assert.Equal(800, doc.RootElement.GetProperty("surplus_w").GetDouble());
        Assert.Equal(300, doc.RootElement.GetProperty("export_w").GetDouble());
    }
}

public class UpstreamStateTests
{
    [Fact]
    public void OneFailureIsTolerated_TwoSwitchToDirect_OneSuccessReturns()
    {
        var state = new UpstreamState();
        Assert.False(state.IsReachable);

        state.Report(true);
        Assert.True(state.IsReachable);

        state.Report(false);
        Assert.True(state.IsReachable);

        state.Report(false);
        Assert.False(state.IsReachable);

        state.Report(true);
        Assert.True(state.IsReachable);
    }

    [Fact]
    public void MarkDown_SwitchesImmediately()
    {
        var state = new UpstreamState();
        state.Report(true);

        state.MarkDown();

        Assert.False(state.IsReachable);
    }
}

public class SecurityTests
{
    [Fact]
    public void Password_VerifiesOnlyTheRightOne()
    {
        var hash = PasswordHash.Create("geheim");
        Assert.True(PasswordHash.Verify("geheim", hash));
        Assert.False(PasswordHash.Verify("falsch", hash));
        Assert.False(PasswordHash.Verify("geheim", "kaputt"));
    }

    [Fact]
    public void Throttle_LocksAfterFiveFailures_AndExpires()
    {
        long now = 1000;
        var throttle = new LoginThrottle(() => now);
        for (var i = 0; i < 4; i++) throttle.Fail("a");
        Assert.False(throttle.IsLocked("a"));

        throttle.Fail("a");
        Assert.True(throttle.IsLocked("a"));
        Assert.False(throttle.IsLocked("b"));

        now += 61;
        Assert.False(throttle.IsLocked("a"));
    }
}

public sealed class BackupAndMqttTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skx-solar").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Backup_WritesUsableCopy_AndPrunesOnlyOwnOldFiles()
    {
        var db = new SolarDb(Path.Combine(_dir, "solar.db"));
        db.Initialize();
        var target = Path.Combine(_dir, "backups");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "solar-2026-01-01.db"), "alt");
        File.WriteAllText(Path.Combine(target, "wichtig.db"), "fremd");

        var file = Path.Combine(target, "solar-2026-06-15.db");
        BackupService.RunBackup(db, target, file, keepDays: 14, new DateTime(2026, 6, 15));

        Assert.True(File.Exists(file));
        Assert.False(File.Exists(file + ".tmp"));
        Assert.False(File.Exists(Path.Combine(target, "solar-2026-01-01.db")));
        Assert.True(File.Exists(Path.Combine(target, "wichtig.db")));
    }

    [Fact]
    public async Task Mqtt_PublishesLiveValuesToARealBroker()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        using var server = new MqttServerFactory().CreateMqttServer(
            new MqttServerOptionsBuilder().WithDefaultEndpoint().WithDefaultEndpointPort(port).Build());
        var received = new TaskCompletionSource<(string Topic, string Payload)>(TaskCreationOptions.RunContinuationsAsynchronously);
        var topics = new System.Collections.Concurrent.ConcurrentBag<string>();
        server.InterceptingPublishAsync += args =>
        {
            topics.Add(args.ApplicationMessage.Topic);
            if (args.ApplicationMessage.Topic == "test/state")
                received.TrySetResult((args.ApplicationMessage.Topic, Encoding.UTF8.GetString(args.ApplicationMessage.Payload)));
            return Task.CompletedTask;
        };
        await server.StartAsync();

        var live = new LiveState();
        live.SetSample(new InverterSample { Ts = 1, PvW = 1234 });
        var options = Options.Create(new SolarOptions { PollSeconds = 1, Mqtt = new MqttOptions { Host = "127.0.0.1", Port = port, Topic = "test/state" } });
        var status = new IntegrationStatus();
        using var service = new MqttService(options, live, new BalconyState(), status, new SettingsSignal());
        await service.StartAsync(CancellationToken.None);

        var message = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await service.StopAsync(CancellationToken.None);
        await server.StopAsync();
        Assert.Equal("test/state", message.Topic);
        Assert.Contains("homeassistant/sensor/skx_solar/pv_w/config", topics);
        Assert.Contains("test/state/availability", topics);
        Assert.Equal(1234, JsonDocument.Parse(message.Payload).RootElement.GetProperty("pv_w").GetDouble());
    }
}

public sealed class AlertStorageTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("skx-solar").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void Alerts_OpenCloseAndHistogram()
    {
        var db = new SolarDb(Path.Combine(_dir, "solar.db"));
        db.Initialize();

        var alert = db.OpenAlert(new AlertRow { Key = "k", Severity = Severity.Warning, Title = "T", Message = "M", StartedTs = 5 });
        Assert.Single(db.OpenAlerts());
        db.MarkAlertNotified(alert.Id, 6);
        db.CloseAlert(alert.Id, 7);

        Assert.Empty(db.OpenAlerts());
        var stored = Assert.Single(db.RecentAlerts(10));
        Assert.Equal(7, stored.EndedTs);
        Assert.Equal(6, stored.NotifiedTs);
        Assert.Equal(10, db.SocHistogram("2000-01-01").Count);
    }

    [Fact]
    public void Store_IgnoresFaultCodesColumn_AndReadsLatestBack()
    {
        var db = new SolarDb(Path.Combine(_dir, "solar.db"));
        db.Initialize();

        db.Store([new InverterSample { Ts = 60, PvW = 5, FaultCodes = "Fehler 0x1" }], TimeZoneInfo.Utc);

        Assert.Equal(5, db.LatestSample()!.PvW);
    }
}

public class SettingsTests
{
    private static PlanningSettings Valid() => new()
    {
        BatteryCapacityKwh = 10,
        InvestmentEur = 15000,
        Tariffs = [new Tariff { From = "2026-06-01", ImportEurPerKwh = 0.3, FeedInEurPerKwh = 0.08 }, new Tariff { From = "2026-01-01", ImportEurPerKwh = 0.32, FeedInEurPerKwh = 0.08 }],
        Forecast = new ForecastOptions { Latitude = 52.5, Longitude = 13.4, Planes = [new PvPlane { Kwp = 5 }] },
        DynamicPrices = new PriceOptions { Enabled = true, Country = "de" },
        Inverter = new InverterSettings { Host = "192.168.1.80", Port = 8899, LoggerSerial = 1234567890 },
        Backup = new BackupOptions { Directory = Path.GetTempPath(), Hour = 4, KeepDays = 30 },
    };

    [Fact]
    public void Save_AppliesInverterAndBackup()
    {
        var options = new SolarOptions { DatabasePath = TempDb() };
        new SettingsStore(Options.Create(options), new SettingsSignal()).Save(Valid());

        Assert.Equal("192.168.1.80", options.Host);
        Assert.Equal(1234567890u, options.LoggerSerial);
        Assert.Equal(4, options.Backup.Hour);
        Assert.Equal(30, options.Backup.KeepDays);
    }

    [Fact]
    public void OlderSavedSettings_KeepInverterFromAppsettings()
    {
        var options = new SolarOptions { DatabasePath = TempDb(), Host = "192.168.1.80", LoggerSerial = 1234567890 };
        options.Backup.Directory = "/mnt/nas";
        // Ein Stand von vor dem Update kennt weder Wechselrichter noch Sicherung.
        Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
        File.WriteAllText(SettingsStore.PathFor(options.DatabasePath), """{ "batteryCapacityKwh": 10 }""");

        SettingsStore.ApplySaved(options);

        Assert.Equal(10, options.BatteryCapacityKwh);
        Assert.Equal("192.168.1.80", options.Host);
        Assert.Equal(1234567890u, options.LoggerSerial);
        Assert.Equal("/mnt/nas", options.Backup.Directory);
    }

    private static string TempDb()
    {
        var directory = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8]);
        return Path.Combine(directory, "solar.db");
    }

    [Fact]
    public void Validate_AcceptsCompleteAndEmptySettings()
    {
        Assert.Null(Valid().Validate());
        Assert.Null(new PlanningSettings().Validate());
    }

    [Theory]
    [InlineData("battery")]
    [InlineData("date")]
    [InlineData("duplicate")]
    [InlineData("price")]
    [InlineData("latitudeOnly")]
    [InlineData("latitudeRange")]
    [InlineData("planeWithoutLocation")]
    [InlineData("kwp")]
    [InlineData("country")]
    [InlineData("nullForecast")]
    [InlineData("inverterHost")]
    [InlineData("inverterPort")]
    [InlineData("loggerSerial")]
    [InlineData("backupDirectory")]
    [InlineData("backupHour")]
    [InlineData("backupKeepDays")]
    public void Validate_RejectsBadValues(string kind)
    {
        var s = Valid();
        switch (kind)
        {
            case "battery": s.BatteryCapacityKwh = 0; break;
            case "date": s.Tariffs[0].From = "morgen"; break;
            case "duplicate": s.Tariffs[0].From = s.Tariffs[1].From; break;
            case "price": s.Tariffs[0].ImportEurPerKwh = -1; break;
            case "latitudeOnly": s.Forecast.Longitude = null; break;
            case "latitudeRange": s.Forecast.Latitude = 91; break;
            case "planeWithoutLocation": s.Forecast.Latitude = null; s.Forecast.Longitude = null; break;
            case "kwp": s.Forecast.Planes[0].Kwp = 0; break;
            case "country": s.DynamicPrices.Country = "fr"; break;
            case "nullForecast": s.Forecast = null!; break;
            case "inverterHost": s.Inverter!.Host = "a/b?c"; break;
            case "inverterPort": s.Inverter!.Port = 0; break;
            case "loggerSerial": s.Inverter!.LoggerSerial = 0; break;
            case "backupDirectory": s.Backup!.Directory = "relativ/ordner"; break;
            case "backupHour": s.Backup!.Hour = 24; break;
            case "backupKeepDays": s.Backup!.KeepDays = 0; break;
        }
        Assert.NotNull(s.Validate());
    }

    [Fact]
    public async Task Save_AppliesImmediatelyWakesServicesAndSurvivesRestart()
    {
        var options = new SolarOptions { DatabasePath = TempDb() };
        var signal = new SettingsSignal();
        var changed = signal.Changed;
        new SettingsStore(Options.Create(options), signal).Save(Valid());

        Assert.Equal(10, options.BatteryCapacityKwh);
        Assert.Equal("2026-01-01", options.Tariffs[0].From);
        Assert.True(options.Forecast.Enabled);
        await changed.WaitAsync(TimeSpan.FromSeconds(1));

        var restarted = new SolarOptions { DatabasePath = options.DatabasePath, BatteryCapacityKwh = 99 };
        SettingsStore.ApplySaved(restarted);
        Assert.Equal(10, restarted.BatteryCapacityKwh);
        Assert.Equal(2, restarted.Tariffs.Count);
        Assert.Equal(5, restarted.Forecast.Planes[0].Kwp);
        Assert.True(restarted.DynamicPrices.Enabled);
    }

    [Theory]
    [InlineData("{ kaputt")]
    [InlineData("{\"batteryCapacityKwh\": -5}")]
    public void ApplySaved_IgnoresUnusableFile(string content)
    {
        var options = new SolarOptions { DatabasePath = TempDb(), BatteryCapacityKwh = 7 };
        var path = SettingsStore.PathFor(options.DatabasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);

        SettingsStore.ApplySaved(options);

        Assert.Equal(7, options.BatteryCapacityKwh);
    }
}

public class PiAndAdviceTests
{
    private const long Now = 1_800_000_000 - 1_800_000_000 % 3600;

    [Fact]
    public void PiHealth_ReadsValuesFromProcAndSys()
    {
        var root = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8]);
        void Put(string path, string text)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), text);
        }
        Put("sys/class/thermal/thermal_zone0/temp", "43470\n");
        Put("proc/loadavg", "0.33 0.21 0.23 2/171 4616\n");
        Put("proc/meminfo", "MemTotal: 926720 kB\nMemAvailable:     204800 kB\n");
        Put("proc/uptime", "86400.00 1000.00\n");
        Put("sys/block/mmcblk0/stat", "1 2 3 4 5 6 2097152 8 0 0 0\n");
        // Der Doppelpunkt im Pfad ist unter Windows nicht erlaubt.
        if (!OperatingSystem.IsWindows())
            Put("sys/devices/platform/soc/soc:firmware/raspberrypi-hwmon/hwmon/hwmon1/in0_lcrit_alarm", "1\n");

        var health = PiHealth.Read(root);

        Assert.Equal(43.47, health.CpuTempC);
        Assert.Equal(0.33, health.Load1);
        Assert.Equal(200, health.MemAvailableMb);
        if (!OperatingSystem.IsWindows())
            Assert.True(health.UnderVoltage);
        Assert.Equal(1024, health.WrittenMbPerDay);
    }

    [Fact]
    public void PiHealth_StaysEmptyWithoutFiles()
    {
        var health = PiHealth.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        Assert.Null(health.CpuTempC);
        Assert.Null(health.UnderVoltage);
        Assert.Null(health.WrittenMbPerDay);
    }

    [Theory]
    [InlineData(81, false, 10, null, "pi_hot")]
    [InlineData(40, true, 10, null, "pi_undervoltage")]
    [InlineData(40, false, 1, null, "disk_low")]
    [InlineData(40, false, 10, 3L * 86400, "backup_stale")]
    public void Alerts_CoverPiProblems(double temp, bool under, double disk, long? backupAge, string key)
    {
        var input = new AlertInput(null, Now, Now - 60, 12, 15, 75, null, null, null, new SystemHealth(temp, under, disk, backupAge));
        Assert.Contains(AlertRules.Evaluate(input, new HashSet<string>()), c => c.Key == key);
    }

    [Fact]
    public void Alerts_QuietWhenPiHealthy()
    {
        var input = new AlertInput(null, Now, Now - 60, 12, 15, 75, null, null, null, new SystemHealth(45, false, 40, 3600));
        Assert.Empty(AlertRules.Evaluate(input, new HashSet<string>()));
    }

    [Fact]
    public void Simulate_FindsFullAndEmptyTimes()
    {
        // 10 kWh, 50 %: 1 kW Verbrauch ohne Sonne erreicht 10 % nach gut vier Stunden.
        var (empty, _) = Advisor.Simulate(Now, 50, 10, 10, _ => 1000, new Dictionary<long, double>());
        Assert.Null(empty.FullTs);
        Assert.InRange(empty.EmptyTs!.Value, Now + 3 * 3600, Now + 4 * 3600);

        // 5 kW Überschuss füllen die Batterie in gut einer Stunde.
        var pv = Enumerable.Range(0, 5).ToDictionary(i => Now + i * 3600L, _ => 6000.0);
        var (full, soc) = Advisor.Simulate(Now, 50, 10, 10, _ => 1000, pv);
        Assert.InRange(full.FullTs!.Value, Now + 3600, Now + 2 * 3600);
        Assert.True(soc[Now + 2 * 3600] >= 98);
    }

    [Fact]
    public void BestWindow_PicksStrongestStretchAndFallsBackToNextDay()
    {
        string Day(long t) => t < Now + 24 * 3600 ? "heute" : "morgen";
        var pv = new Dictionary<long, double>
        {
            [Now + 1 * 3600] = 2000, [Now + 3 * 3600] = 4000, [Now + 4 * 3600] = 4000, [Now + 6 * 3600] = 100,
        };
        var window = Advisor.BestWindow(Now, _ => 500, pv, new Dictionary<long, double> { [Now + 3 * 3600] = 100 }, Day)!;
        Assert.Equal(Now + 3 * 3600, window.StartTs);
        Assert.Equal(Now + 5 * 3600, window.EndTs);
        Assert.Equal(3500, window.AvgW);
        Assert.True(window.BatteryFull);

        var tomorrow = new Dictionary<long, double> { [Now + 1 * 3600] = 100, [Now + 30 * 3600] = 3000 };
        Assert.Equal(Now + 30 * 3600, Advisor.BestWindow(Now, _ => 500, tomorrow, new Dictionary<long, double>(), Day)!.StartTs);
        Assert.Null(Advisor.BestWindow(Now, _ => 500, new Dictionary<long, double>(), new Dictionary<long, double>(), Day));
    }

    [Fact]
    public void Accuracy_SuggestsRatioOnlyFromDaysWithoutFullBattery()
    {
        var rows = Enumerable.Range(1, 7).Select(d => new ForecastActual { Date = $"2026-06-{d:00}", NormKwh = 20, PvKwh = 14, MaxSoc = 80 })
            .Append(new ForecastActual { Date = "2026-06-08", NormKwh = 20, PvKwh = 2, MaxSoc = 100 }).ToList();

        var result = Advisor.Accuracy(rows, 0.8);

        Assert.Equal(0.7, result.SuggestedRatio);
        Assert.Equal(7, result.UsableDays);
        Assert.Equal(16, result.Days[0].ForecastKwh);
        Assert.NotNull(result.DeviationPct);
        Assert.Null(Advisor.Accuracy(rows.Take(6).ToList(), 0.8).SuggestedRatio);
    }

    [Fact]
    public void SaveForecast_KeepsDayBeforeValueForToday()
    {
        var path = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var db = new SolarDb(path);
        db.Initialize();
        db.SaveForecast("2026-06-01", [("2026-06-02", 10.0)]);
        db.SaveForecast("2026-06-02", [("2026-06-02", 99.0), ("2026-06-03", 5.0)]);
        db.Store([new InverterSample { Ts = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), TotalPvKwh = 3, TodayPvKwh = 3 }], TimeZoneInfo.Utc);

        Assert.Equal(10, db.ForecastActuals("2026-06-01", "2026-06-03").Single().NormKwh);
    }

    [Fact]
    public void Store_ThinsRawSamplesButKeepsMinuteAverage()
    {
        var path = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8] + ".db");
        var db = new SolarDb(path);
        db.Initialize();
        var start = new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        db.Store(Enumerable.Range(0, 6).Select(i => new InverterSample { Ts = start + i * 10, PvW = i * 100 }).ToList(), TimeZoneInfo.Utc, 60);

        Assert.Equal(500, db.LatestSample()!.PvW);
        Assert.Equal(250, db.Minutes("2026-06-02").Single().PvW);
        Assert.Equal(0, db.DeleteSamplesBefore(start));
        Assert.Equal(1, db.DeleteSamplesBefore(start + 3600));
    }

    [Fact]
    public void Discovery_DescribesSensorsForHomeAssistant()
    {
        var sensors = MqttPayload.Discovery("skx-solar/state").ToList();
        var pv = sensors.Single(s => s.Topic == "homeassistant/sensor/skx_solar/pv_w/config");
        using var doc = JsonDocument.Parse(pv.Payload);
        Assert.Equal("skx-solar/state", doc.RootElement.GetProperty("state_topic").GetString());
        Assert.Equal("{{ value_json.pv_w }}", doc.RootElement.GetProperty("value_template").GetString());
        Assert.Equal("W", doc.RootElement.GetProperty("unit_of_measurement").GetString());

        using var state = JsonDocument.Parse(sensors.Single(s => s.Topic.Contains("/state/")).Payload);
        Assert.False(state.RootElement.TryGetProperty("unit_of_measurement", out _));

        using var payload = JsonDocument.Parse(MqttPayload.Build(new InverterSample { TotalPvKwh = 12 }));
        Assert.Equal(12, payload.RootElement.GetProperty("total_pv_kwh").GetDouble());
    }

    [Fact]
    public void Settings_MaskSecretsAndKeepThemWhenFormSendsNothing()
    {
        var options = new SolarOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8], "solar.db") };
        var store = new SettingsStore(Options.Create(options), new SettingsSignal());
        var first = new PlanningSettings();
        first.Alerts.TelegramBotToken = "geheim";
        first.Mqtt.Password = "auch";
        store.Save(first);

        var masked = store.Current.Masked();
        Assert.Null(masked.Alerts.TelegramBotToken);
        Assert.Null(masked.Mqtt.Password);
        Assert.True(masked.TelegramTokenSet);
        Assert.DoesNotContain("geheim", JsonSerializer.Serialize(masked));

        store.Save(masked);
        Assert.Equal("geheim", options.Alerts.TelegramBotToken);

        var cleared = store.Current.Masked();
        cleared.Alerts.TelegramBotToken = "";
        store.Save(cleared);
        Assert.Null(options.Alerts.TelegramBotToken);
        Assert.Equal("auch", options.Mqtt.Password);
    }

    [Fact]
    public void Password_FirstSetupOnlyOnceAndSurvivesRestart()
    {
        var (options, store) = NewStore();

        Assert.True(store.CreateFirstUser("sky", "erstes-passwort"));
        Assert.False(store.CreateFirstUser("fremd", "fremdes-passwort"));
        Assert.Equal(Roles.Admin, options.Auth.Find("SKY")!.Role);
        Assert.Null(options.Auth.Find("fremd"));

        var restarted = new SolarOptions { DatabasePath = options.DatabasePath };
        SettingsStore.ApplySaved(restarted);
        Assert.True(PasswordHash.Verify("erstes-passwort", restarted.Auth.Find("sky")!.PasswordHash));

        Assert.Null(store.UpdateUser("sky", null, "zweites-passwort"));
        Assert.True(PasswordHash.Verify("zweites-passwort", options.Auth.Find("sky")!.PasswordHash));
    }

    [Fact]
    public void Auth_OldPasswordFileBecomesAdminAccount()
    {
        var (options, _) = NewStore();
        var folder = Path.GetDirectoryName(options.DatabasePath)!;
        Directory.CreateDirectory(folder);
        var hash = PasswordHash.Create("altes-passwort");
        File.WriteAllText(Path.Combine(folder, "auth.json"), $$"""{ "passwordHash": "{{hash}}" }""");

        var restarted = new SolarOptions { DatabasePath = options.DatabasePath };
        SettingsStore.ApplySaved(restarted);

        var admin = Assert.Single(restarted.Auth.Users);
        Assert.Equal("admin", admin.Name);
        Assert.Equal(Roles.Admin, admin.Role);
        Assert.True(PasswordHash.Verify("altes-passwort", admin.PasswordHash));
    }

    [Fact]
    public void Users_RulesProtectNamesRolesAndTheLastAdmin()
    {
        var (options, store) = NewStore();
        store.CreateFirstUser("admin", "erstes-passwort");

        Assert.Null(store.AddUser("anna", "leser-passwort", Roles.Viewer));
        Assert.NotNull(store.AddUser("ANNA", "leser-passwort", Roles.Viewer));
        Assert.NotNull(store.AddUser("mit leerzeichen", "leser-passwort", Roles.Viewer));
        Assert.NotNull(store.AddUser("kurz", "kurz", Roles.Viewer));
        Assert.NotNull(store.AddUser("rolle", "leser-passwort", "root"));

        Assert.NotNull(store.UpdateUser("admin", Roles.Viewer, null));
        Assert.NotNull(store.RemoveUser("admin"));
        Assert.Equal(Roles.Admin, options.Auth.Find("admin")!.Role);

        var before = SettingsStore.AccountStamp(options.Auth.Find("anna")!);
        Assert.Null(store.UpdateUser("anna", Roles.Admin, null));
        Assert.NotEqual(before, SettingsStore.AccountStamp(options.Auth.Find("anna")!));
        Assert.Null(store.RemoveUser("admin"));
        Assert.Null(options.Auth.Find("admin"));
    }

    [Theory]
    [InlineData("GET", "/api/live", false, true)]
    [InlineData("GET", "/api/export/days.csv", false, true)]
    [InlineData("GET", "/api/settings", false, false)]
    [InlineData("GET", "/API/Settings/", false, false)]
    [InlineData("GET", "/api/users", false, false)]
    [InlineData("GET", "/api/export/snapshot.db", false, false)]
    [InlineData("PUT", "/api/settings", false, false)]
    [InlineData("POST", "/api/users", false, false)]
    [InlineData("DELETE", "/api/users/anna", false, false)]
    [InlineData("POST", "/api/notifications/test", false, false)]
    [InlineData("POST", "/api/neu", false, false)]
    [InlineData("POST", "/api/password", false, true)]
    [InlineData("POST", "/api/logout", false, true)]
    [InlineData("POST", "/api/login", false, true)]
    [InlineData("GET", "/index.html", false, true)]
    [InlineData("PUT", "/api/settings", true, true)]
    [InlineData("GET", "/api/users", true, true)]
    public void Access_ViewerOnlyReads(string method, string path, bool admin, bool allowed) =>
        Assert.Equal(allowed, AccessRules.Allows(method, path, admin));

    [Fact]
    public void Login_UnknownAccountFailsLikeAWrongPassword()
    {
        Assert.False(PasswordHash.VerifyAccount("geheim", null));
        Assert.True(PasswordHash.VerifyAccount("geheim", PasswordHash.Create("geheim")));
    }

    private static (SolarOptions Options, SettingsStore Store) NewStore()
    {
        var options = new SolarOptions { DatabasePath = Path.Combine(Path.GetTempPath(), "skx" + Guid.NewGuid().ToString("N")[..8], "solar.db") };
        return (options, new SettingsStore(Options.Create(options), new SettingsSignal()));
    }
}
