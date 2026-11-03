using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using SKx.Solar.Alerts;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Integrations;
using SKx.Solar.Storage;
using Xunit;

namespace SKx.Solar.Tests;

public class MirrorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "skx-mirror-" + Guid.NewGuid().ToString("N"));
    private const long Start = 1_790_000_040;

    private sealed record Side(SolarOptions Options, SolarDb Db, LiveState Live, BalconyState Balcony, ForecastService Forecast, PriceService Prices);

    private Side Create(string name, SolarOptions? options = null)
    {
        var o = options ?? new SolarOptions();
        o.DatabasePath = Path.Combine(_folder, name, "solar.db");
        var db = new SolarDb(o.DatabasePath);
        db.Initialize();
        var wrapped = Options.Create(o);
        var signal = new SettingsSignal();
        var status = new IntegrationStatus();
        return new Side(o, db, new LiveState(), new BalconyState(),
            new ForecastService(wrapped, new NoHttp(), status, signal, db), new PriceService(wrapped, new NoHttp(), status, signal));
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException();
    }

    private static MirrorSource Source(Side s) => new(Options.Create(s.Options), s.Db, s.Live, s.Balcony, s.Forecast, s.Prices);

    private static MirrorReceiver Receiver(Side s) => new(Options.Create(s.Options), s.Db, s.Live, s.Balcony, s.Forecast, s.Prices, new IntegrationStatus());

    private static InverterSample Sample(long ts, double total) => new()
    {
        Ts = ts, PvW = 1000, LoadW = 400, BatterySoc = 50, TotalPvKwh = total, TotalLoadKwh = total / 2, TodayPvKwh = 1,
    };

    private static void Run(MirrorSource source, MirrorReceiver receiver, long now)
    {
        MirrorAck? ack = null;
        for (var round = 0; round < 100; round++)
        {
            var (push, more) = source.Next(ack, now);
            ack = receiver.Apply(push, now);
            source.Confirm(push);
            if (!more)
                return;
        }
        throw new InvalidOperationException("Der Abgleich endet nicht.");
    }

    [Fact]
    public void Sync_CopiesHistoryInBatchesAndMatchesTheDaySummary()
    {
        var pi = Create("pi");
        // Mehr als ein Paket fasst.
        pi.Db.Store(Enumerable.Range(0, 2500).Select(i => Sample(Start + i * 60L, 100 + i * 0.01)).ToList(), TimeZoneInfo.Utc);
        var server = Create("server");
        Run(Source(pi), Receiver(server), Start + 200000);

        Assert.Equal(2500, server.Db.MinutesSince(0, 10000).Count);
        var date = pi.Db.AllDays()[^1].Date;
        Assert.Equal(pi.Db.Day(date)!.PvKwh, server.Db.Day(date)!.PvKwh);
    }

    [Fact]
    public void Sync_CatchesUpAfterAnOutageAndDuplicatesChangeNothing()
    {
        var pi = Create("pi");
        var server = Create("server");
        var source = Source(pi);
        var receiver = Receiver(server);
        pi.Db.Store([Sample(Start, 100)], TimeZoneInfo.Utc);
        Run(source, receiver, Start + 60);
        pi.Db.Store(Enumerable.Range(1, 600).Select(i => Sample(Start + i * 60L, 100 + i * 0.01)).ToList(), TimeZoneInfo.Utc);

        Run(source, receiver, Start + 40000);
        Run(source, receiver, Start + 40030);

        Assert.Equal(601, server.Db.MinutesSince(0, 10000).Count);
    }

    [Fact]
    public void Sync_RefillsAnEmptyServerDatabase()
    {
        var pi = Create("pi");
        var source = Source(pi);
        pi.Db.Store([Sample(Start, 100)], TimeZoneInfo.Utc);
        Run(source, Receiver(Create("first")), Start + 60);

        var fresh = Create("fresh");
        Run(source, Receiver(fresh), Start + 90);

        Assert.Single(fresh.Db.MinutesSince(0, 10));
        Assert.NotNull(fresh.Db.Day(Aggregation.LocalDate(Start, TimeZoneInfo.Utc)));
    }

    [Fact]
    public void Sync_SendsUnsavedMinutesWithoutSkippingTheirFinalValues()
    {
        var pi = Create("pi");
        var server = Create("server");
        var source = Source(pi);
        var receiver = Receiver(server);
        pi.Live.Pending = [Sample(Start, 100)];
        Run(source, receiver, Start + 30);
        Assert.Single(server.Db.MinutesSince(0, 10));

        pi.Live.Pending = [];
        pi.Db.Store([Sample(Start, 100) with { PvW = 2000 }], TimeZoneInfo.Utc);
        Run(source, receiver, Start + 330);

        Assert.Equal(2000, server.Db.MinutesSince(0, 10)[0].PvW);
    }

    [Fact]
    public void Sync_SendsPlanningOnlyAfterAChangeAndNeverAnySecret()
    {
        var pi = Create("pi", new SolarOptions
        {
            InvestmentEur = 1000,
            Alerts = new AlertOptions { TelegramBotToken = "telegram-geheim", NtfyTopic = "thema-geheim", TelegramChatId = "chat-geheim" },
            Mqtt = new MqttOptions { Host = "10.0.0.9", Password = "mqtt-geheim" },
            Sync = new SyncOptions { Url = "https://solar.example.de", Token = "sync-geheim" },
            Balcony = new BalconyOptions { Host = "10.0.0.27" },
            Host = "10.0.0.80",
            LoggerSerial = 1234567890,
            Backup = new BackupOptions { Directory = "/mnt/nas-geheim" },
        });
        pi.Options.Auth.Users = [new AuthUser { Name = "leser", Role = Roles.Viewer, PasswordHash = "hash" }];
        var server = Create("server");
        var source = Source(pi);
        var receiver = Receiver(server);

        var (first, _) = source.Next(null, Start);
        var ack = receiver.Apply(first, Start);
        Assert.True(ack.NeedsPlanning);
        var (second, _) = source.Next(ack, Start);
        var json = JsonSerializer.Serialize(second.Planning);
        foreach (var secret in new[] { "telegram-geheim", "thema-geheim", "chat-geheim", "10.0.0.9", "mqtt-geheim", "sync-geheim", "10.0.0.27", "10.0.0.80", "1234567890", "nas-geheim" })
            Assert.DoesNotContain(secret, json);
        ack = receiver.Apply(second, Start);
        source.Confirm(second);

        Assert.False(ack.NeedsPlanning);
        Assert.Equal(1000, server.Options.InvestmentEur);
        Assert.Equal(Roles.Viewer, server.Options.Auth.Find("leser")!.Role);
        Assert.Null(source.Next(ack, Start).Push.Planning);
        pi.Options.InvestmentEur = 2000;
        Assert.NotNull(source.Next(ack, Start).Push.Planning);
    }

    [Fact]
    public void Receiver_RestoresLiveValuesAndPasswordAfterARestart()
    {
        var pi = Create("pi");
        pi.Options.Auth.Users = [new AuthUser { Name = "leser", Role = Roles.Viewer, PasswordHash = "hash" }];
        pi.Live.SetSample(Sample(Start, 100));
        var server = Create("server");
        Run(Source(pi), Receiver(server), Start);

        var restarted = Create("server");
        var receiver = Receiver(restarted);
        receiver.Restore();

        Assert.Equal(Start, restarted.Live.Current.Sample!.Ts);
        Assert.Equal("hash", restarted.Options.Auth.Find("leser")!.PasswordHash);
        Assert.Equal(Start, receiver.LastPushTs);
    }

    [Theory]
    [InlineData("Bearer richtig", "richtig", true)]
    [InlineData("Bearer falsch", "richtig", false)]
    [InlineData("richtig", "richtig", false)]
    [InlineData(null, "richtig", false)]
    [InlineData("Bearer ", "", false)]
    [InlineData("Bearer x", null, false)]
    public void Token_MustMatch(string? header, string? expected, bool result) =>
        Assert.Equal(result, MirrorReceiver.TokenMatches(header, expected));

    [Theory]
    [InlineData("/api/login", true)]
    [InlineData("/api/mirror/push", true)]
    [InlineData("/api/settings", false)]
    [InlineData("/api/password", false)]
    [InlineData("/api/setup-password", false)]
    [InlineData("/api/notifications/test", false)]
    public void Mirror_BlocksAllChanges(string path, bool allowed) =>
        Assert.Equal(allowed, MirrorReceiver.AllowsWrite(path));

    [Theory]
    [InlineData("https://solar.example.de", true)]
    [InlineData("http://localhost:8080", true)]
    [InlineData("http://solar.example.de", false)]
    [InlineData("solar.example.de", false)]
    public void Settings_RefuseUnencryptedMirror(string url, bool valid) =>
        Assert.Equal(valid, new PlanningSettings { Sync = new SyncOptions { Url = url } }.Validate() is null);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
