using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using SKx.Solar.Alerts;
using SKx.Solar.Collector;
using SKx.Solar.Deye;
using SKx.Solar.Integrations;
using SKx.Solar.Security;
using SKx.Solar.Storage;

if (args.Contains("--hash-password"))
{
    Console.Write("Passwort: ");
    var password = Console.ReadLine();
    if (string.IsNullOrEmpty(password))
        return 1;
    Console.WriteLine(PasswordHash.Create(password));
    return 0;
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.Host.UseSystemd();
builder.Services.Configure<SolarOptions>(builder.Configuration.GetSection("Solar"));
builder.Services.PostConfigure<SolarOptions>(SettingsStore.ApplySaved);
builder.Services.AddSingleton<SettingsSignal>();
builder.Services.AddSingleton<SettingsStore>();
builder.Services.AddHttpClient();
builder.Services.ConfigureHttpClientDefaults(http => http.ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(20)));
// Exporte vom Pi dürfen dauern, die Wartezeit auf die Antwort begrenzt die Weiterleitung selbst.
builder.Services.AddHttpClient(UpstreamMonitor.ClientName, c => c.Timeout = Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<UpstreamMonitor>();
builder.Services.AddSingleton<LiveState>();
builder.Services.AddSingleton<IntegrationStatus>();
builder.Services.AddSingleton<LoginThrottle>(_ => new LoginThrottle());
builder.Services.AddSingleton(sp =>
{
    var db = new SolarDb(sp.GetRequiredService<IOptions<SolarOptions>>().Value.DatabasePath);
    db.Initialize();
    return db;
});
builder.Services.AddSingleton<ForecastService>();
builder.Services.AddSingleton<PriceService>();
builder.Services.AddSingleton<BalconyState>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<MirrorSource>();
builder.Services.AddSingleton<MirrorReceiver>();

// Der Spiegel liest nichts selbst: Er füllt dieselben Quellen nur mit dem, was der Pi schickt.
var mirrorMode = builder.Configuration.GetValue<bool>("Solar:Mirror:Enabled");
if (mirrorMode && string.IsNullOrWhiteSpace(builder.Configuration["Solar:Mirror:Token"]))
    throw new InvalidOperationException("Der Spiegel braucht Solar:Mirror:Token.");
if (!mirrorMode)
{
    builder.Services.AddHostedService(sp => sp.GetRequiredService<UpstreamMonitor>());
    builder.Services.AddHostedService(sp => new CollectorService(
        sp.GetRequiredService<IOptions<SolarOptions>>(),
        sp.GetRequiredService<SolarDb>(),
        sp.GetRequiredService<LiveState>(),
        sp.GetRequiredService<ILogger<CollectorService>>(),
        sp.GetRequiredService<UpstreamMonitor>()));
    builder.Services.AddHostedService<AlertService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ForecastService>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<PriceService>());
    builder.Services.AddHostedService<MqttService>();
    builder.Services.AddHostedService<WatchdogService>();
    builder.Services.AddHostedService<BalconyService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
    builder.Services.AddHostedService<SyncService>();
}

var keyDirectory = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(builder.Configuration["Solar:DatabasePath"] ?? "data/solar.db"))!, "keys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyDirectory));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
{
    cookie.Cookie.Name = "skx-solar";
    cookie.Cookie.HttpOnly = true;
    cookie.Cookie.SameSite = SameSiteMode.Strict;
    cookie.ExpireTimeSpan = TimeSpan.FromDays(30);
    cookie.SlidingExpiration = true;
    // Nach einer Änderung an Passwort oder Rolle und beim Löschen gelten ältere Anmeldungen nicht mehr.
    cookie.Events.OnValidatePrincipal = async context =>
    {
        var auth = context.HttpContext.RequestServices.GetRequiredService<IOptions<SolarOptions>>().Value.Auth;
        if (auth.Find(context.Principal?.Identity?.Name) is { } user && context.Principal?.FindFirstValue(AccountStampClaim) == SettingsStore.AccountStamp(user))
            return;
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync();
    };
});
// Caddy läuft auf demselben Gerät: Nur von dort gelten die Angaben zu Besucher und HTTPS, das sichert auch das Cookie.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Im Container ist Caddy ein anderer Rechner im Docker-Netz, und nur er erreicht den Dienst.
    if (mirrorMode)
    {
        o.KnownProxies.Clear();
        o.KnownIPNetworks.Clear();
    }
});

var app = builder.Build();
app.UseForwardedHeaders();
if (mirrorMode)
{
    app.Services.GetRequiredService<MirrorReceiver>().Restore();
    app.Use(async (context, next) =>
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method) || MirrorReceiver.AllowsWrite(context.Request.Path.Value ?? ""))
            await next();
        else
            await Results.Json(new { error = "Der Spiegel zeigt nur an. Ändern lässt sich das auf dem Pi." }, statusCode: 403).ExecuteAsync(context);
    });
}
string[] publicPaths = ["/login.html", "/login.js", "/app.css", "/api/login", "/api/auth-state", "/api/setup-password", "/api/connection", "/api/mirror/push", "/health", "/manifest.webmanifest", "/icons/icon-192.png", "/icons/icon-512.png"];

app.MapWhen(context => context.Request.Path.StartsWithSegments("/api")
        && !context.Request.Path.StartsWithSegments("/api/connection")
        && context.RequestServices.GetRequiredService<UpstreamMonitor>().IsReachable,
    branch => branch.Run(async context =>
    {
        var upstream = context.RequestServices.GetRequiredService<UpstreamMonitor>();
        if (!await UpstreamProxy.TryForwardAsync(context, upstream, context.RequestServices.GetRequiredService<IHttpClientFactory>()))
        {
            // Die Seite lädt beim nächsten Abruf automatisch die direkt gelesenen Werte.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        }
    }));

app.UseAuthentication();
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "/";
    // Über den Pi gilt dessen Anmeldung: Seine API prüft, die lokalen Dateien der Oberfläche sind nichts Geheimes.
    var viaPi = context.RequestServices.GetRequiredService<UpstreamMonitor>().IsReachable;
    if (viaPi || context.User.Identity?.IsAuthenticated == true || publicPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
    {
        await next();
        return;
    }
    if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    else
        context.Response.Redirect("/login.html");
});
app.Use(async (context, next) =>
{
    if (AccessRules.Allows(context.Request.Method, context.Request.Path.Value ?? "/", context.User.IsInRole(Roles.Admin)))
        await next();
    else
        await Results.Json(new { error = "Dein Konto darf nur lesen." }, statusCode: 403).ExecuteAsync(context);
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    // Nach einem Update sollen Browser sofort die neue Oberfläche laden, die ETag-Prüfung spart trotzdem Traffic.
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

var snapshotLock = new SemaphoreSlim(1, 1);
var startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var api = app.MapGroup("/api");

api.MapGet("/connection", (UpstreamMonitor upstream, MirrorReceiver mirror) => new { configured = upstream.Configured, viaPi = upstream.IsReachable, mirror = mirrorMode, lastPushTs = mirrorMode ? mirror.LastPushTs : 0 });

if (mirrorMode)
{
    api.MapPost("/mirror/push", async (HttpContext context, IOptions<SolarOptions> options, MirrorReceiver mirror, CancellationToken ct) =>
    {
        // Erst das Token: Fremde Anfragen sollen gar nicht erst gelesen werden.
        if (!MirrorReceiver.TokenMatches(context.Request.Headers.Authorization, options.Value.Mirror.Token))
            return Results.Unauthorized();
        var push = await context.Request.ReadFromJsonAsync<MirrorPush>(ct);
        return push is null ? Results.BadRequest() : Results.Ok(mirror.Apply(push, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    });
}

api.MapGet("/auth-state", (IOptions<SolarOptions> options) => new { passwordSet = mirrorMode || options.Value.Auth.Enabled, minLength = SettingsStore.MinPasswordLength });

api.MapPost("/setup-password", async (LoginRequest request, HttpContext context, SettingsStore store) =>
{
    var name = string.IsNullOrWhiteSpace(request.Username) ? Roles.Admin : request.Username.Trim();
    if ((SettingsStore.NameProblem(name) ?? SettingsStore.PasswordProblem(request.Password)) is { } problem)
        return Results.Json(new { error = problem }, statusCode: 400);
    if (!store.CreateFirstUser(name, request.Password!))
        return Results.Json(new { error = "Es wurde bereits ein Konto angelegt." }, statusCode: 409);
    await SignInAsync(context, name);
    return Results.Ok();
});

api.MapPost("/password", async (PasswordChange request, HttpContext context, IOptions<SolarOptions> options, SettingsStore store, LoginThrottle throttle) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
    if (throttle.IsLocked(address))
        return Results.Json(new { error = "Zu viele Versuche. Bitte in einer Minute erneut probieren." }, statusCode: 429);
    var name = context.User.Identity?.Name ?? "";
    if (!PasswordHash.VerifyAccount(request.Current ?? "", options.Value.Auth.Find(name)?.PasswordHash))
    {
        throttle.Fail(address);
        return Results.Json(new { error = "Das aktuelle Passwort stimmt nicht." }, statusCode: 400);
    }
    if (store.UpdateUser(name, null, request.Next ?? "") is { } problem)
        return Results.Json(new { error = problem }, statusCode: 400);
    throttle.Success(address);
    // Dieses Gerät bleibt angemeldet, alle anderen müssen das neue Passwort eingeben.
    await SignInAsync(context, name);
    return Results.Ok();
});

api.MapGet("/me", (HttpContext context) => new { name = context.User.Identity?.Name, admin = context.User.IsInRole(Roles.Admin) });

api.MapGet("/users", (IOptions<SolarOptions> options) => options.Value.Auth.Users.Select(u => new { name = u.Name, role = u.Role }));

api.MapPost("/users", (UserRequest request, SettingsStore store) =>
    store.AddUser(request.Username?.Trim(), request.Password, request.Role) is { } problem
        ? Results.Json(new { error = problem }, statusCode: 400)
        : Results.Ok());

// Das eigene Konto bleibt gesperrt: So kann sich kein Admin versehentlich selbst aussperren.
api.MapPut("/users/{name}", (string name, UserRequest request, HttpContext context, SettingsStore store) =>
    IsSelf(context, name) ? Results.Json(new { error = "Dein eigenes Konto kannst du hier nicht ändern." }, statusCode: 400)
    : store.UpdateUser(name, request.Role, string.IsNullOrEmpty(request.Password) ? null : request.Password) is { } problem
        ? Results.Json(new { error = problem }, statusCode: 400)
        : Results.Ok());

api.MapDelete("/users/{name}", (string name, HttpContext context, SettingsStore store) =>
    IsSelf(context, name) ? Results.Json(new { error = "Dein eigenes Konto kannst du nicht löschen." }, statusCode: 400)
    : store.RemoveUser(name) is { } problem
        ? Results.Json(new { error = problem }, statusCode: 400)
        : Results.Ok());

api.MapPost("/login", async (LoginRequest request, HttpContext context, IOptions<SolarOptions> options, LoginThrottle throttle) =>
{
    var address = context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
    if (!options.Value.Auth.Enabled)
        return Results.NotFound();
    if (throttle.IsLocked(address))
        return Results.Json(new { error = "Zu viele Versuche. Bitte in einer Minute erneut probieren." }, statusCode: 429);
    var user = options.Value.Auth.Find(request.Username?.Trim());
    if (!PasswordHash.VerifyAccount(request.Password ?? "", user?.PasswordHash))
    {
        throttle.Fail(address);
        return Results.Json(new { error = "Benutzername oder Passwort stimmt nicht." }, statusCode: 401);
    }
    throttle.Success(address);
    await SignInAsync(context, user!.Name);
    return Results.Ok();
});

api.MapPost("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync();
    return Results.Ok();
});

api.MapGet("/live", (LiveState live, BalconyState balcony, SolarDb db, IOptions<SolarOptions> options) =>
{
    var state = live.Current;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var today = DateTimeOffset.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var energy = db.Day(today);
    var flows = energy is null ? null : EnergyFlows.Compute(energy, db.DayMinuteFlows(today));
    var openAlerts = db.OpenAlerts();
    return new
    {
        sample = state.Sample,
        serverNow = now,
        ageSeconds = state.Sample is null ? (long?)null : now - state.Sample.Ts,
        stateName = state.Sample is null ? null : DeyeDecoder.StateName(state.Sample.State),
        device = state.Device,
        error = state.LastError,
        pollSeconds = options.Value.PollSeconds,
        today = energy,
        todayFlows = flows,
        todaySavingsEur = energy is null ? null : Round2(Economics.Savings(energy, options.Value.Tariffs)),
        openAlerts = openAlerts.Count,
        worstAlert = openAlerts.MaxBy(a => Severity.Rank(a.Severity))?.Severity,
        authEnabled = options.Value.Auth.Enabled,
        balcony = new
        {
            configured = !string.IsNullOrWhiteSpace(options.Value.Balcony.Host),
            w = Math.Round(balcony.W(now)),
            lastSeenTs = balcony.Latest?.Ts,
        },
    };
});

api.MapGet("/day", (string? date, SolarDb db, LiveState live, BalconyState balcony, IOptions<SolarOptions> options) =>
{
    if (date is not null && ParseDate(date) is null)
        return Results.BadRequest("Ungültiges Datum.");
    var day = ParseDate(date) ?? DateOnly.FromDateTime(DateTime.Now);
    var text = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var energy = db.Day(text);
    var minutes = db.Minutes(text).ToList();
    // Im SD-Karten-Modus liegen die letzten Minuten noch im Speicher.
    var stored = minutes.Count > 0 ? minutes[^1].Ts : 0;
    minutes.AddRange(Aggregation.ToMinutes(live.Pending, TimeZoneInfo.Local).Where(m => m.LocalDate == text && m.Ts > stored));
    var pendingBalcony = balcony.PendingMinutes();
    foreach (var minute in minutes)
    {
        if (pendingBalcony.TryGetValue(minute.Ts, out var w))
            minute.BalconyW = w;
    }
    return Results.Ok(new
    {
        date = text,
        energy,
        savingsEur = energy is null ? null : Round2(Economics.Savings(energy, options.Value.Tariffs)),
        points = minutes.Select(m => new
        {
            t = m.Ts,
            pv = Math.Round(m.PvW + m.BalconyW),
            roof = Math.Round(m.PvW),
            load = Math.Round(m.LoadW + m.BalconyW),
            balconyW = Math.Round(m.BalconyW),
            grid = Math.Round(m.GridW),
            battery = Math.Round(m.BatteryW),
            soc = m.BatterySoc,
        }),
    });
});

api.MapGet("/month", (string? month, SolarDb db, IOptions<SolarOptions> options) =>
{
    if (!DateOnly.TryParseExact((month ?? DateTime.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture)) + "-01",
            "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var first))
        return Results.BadRequest("Ungültiger Monat.");
    var days = db.Days(
        first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        first.AddMonths(1).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    return Results.Ok(new
    {
        month = first.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        days,
        pvKwh = Math.Round(days.Sum(d => d.PvKwh), 1),
        balconyKwh = Math.Round(days.Sum(d => d.BalconyKwh), 1),
        loadKwh = Math.Round(days.Sum(d => d.LoadKwh), 1),
        importKwh = Math.Round(days.Sum(d => d.ImportKwh), 1),
        exportKwh = Math.Round(days.Sum(d => d.ExportKwh), 1),
        savingsEur = Economics.Sum(days, options.Value.Tariffs),
    });
});

api.MapGet("/days", (string? from, string? to, SolarDb db) =>
{
    if (ParseDate(from) is not { } first || ParseDate(to) is not { } last)
        return Results.BadRequest("Ungültiges Datum.");
    return Results.Ok(db.Days(
        first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
});

api.MapGet("/year", (string? year, SolarDb db, IOptions<SolarOptions> options) =>
{
    var value = year ?? DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
    if (value.Length != 4 || !value.All(char.IsAsciiDigit))
        return Results.BadRequest("Ungültiges Jahr.");
    var months = db.Months(value);
    return Results.Ok(new
    {
        year = value,
        months,
        pvKwh = Math.Round(months.Sum(d => d.PvKwh), 1),
        balconyKwh = Math.Round(months.Sum(d => d.BalconyKwh), 1),
        loadKwh = Math.Round(months.Sum(d => d.LoadKwh), 1),
        importKwh = Math.Round(months.Sum(d => d.ImportKwh), 1),
        exportKwh = Math.Round(months.Sum(d => d.ExportKwh), 1),
        savingsEur = Economics.Sum(db.Days($"{value}-01-01", $"{value}-12-31"), options.Value.Tariffs),
    });
});

api.MapGet("/alerts", (SolarDb db) => db.RecentAlerts(200));

api.MapGet("/insights", (SolarDb db, LiveState live, MirrorReceiver mirror, IOptions<SolarOptions> options) =>
{
    var o = options.Value;
    var today = DateOnly.FromDateTime(DateTime.Now);
    var since = today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var todayText = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var recent = db.Days(since, todayText);
    var charged = recent.Sum(d => d.ChargeKwh);
    var discharged = recent.Sum(d => d.DischargeKwh);
    var sample = live.Current.Sample;
    var temp = db.BatteryTempRange(since);
    // Der Spiegel bekommt keine Rohwerte, darum rechnet das der Pi.
    var strings = mirrorMode ? mirror.Strings : db.StringAverages(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 30 * 86400L);

    var year = today.Year.ToString(CultureInfo.InvariantCulture);
    var previous = (today.Year - 1).ToString(CultureInfo.InvariantCulture);
    return new
    {
        battery = new
        {
            capacityKwh = o.BatteryCapacityKwh,
            cycles = o.BatteryCapacityKwh is > 0 && sample is not null ? Math.Round(sample.TotalDischargeKwh / o.BatteryCapacityKwh.Value, 1) : (double?)null,
            chargedKwh = Math.Round(charged, 1),
            dischargedKwh = Math.Round(discharged, 1),
            // Erst über mehrere Ladezyklen aussagekräftig, weil der Ladestand zu Beginn und Ende abweichen kann.
            efficiencyPct = charged >= 20 ? Math.Round(discharged / charged * 100, 1) : (double?)null,
            socHistogram = db.SocHistogram(since),
            tempMinC = temp.Min,
            tempMaxC = temp.Max,
        },
        strings = new { pv1W = strings.Pv1W, pv2W = strings.Pv2W },
        bestDays = db.BestDays(5),
        year,
        previousYear = previous,
        months = db.Months(year),
        previousMonths = db.Months(previous),
    };
});

api.MapGet("/economy", (SolarDb db, IOptions<SolarOptions> options) =>
{
    var o = options.Value;
    var days = db.AllDays();
    var total = Economics.Sum(days, o.Tariffs);
    // Tage vor dem ersten Tarif haben keine Ersparnis und würden die Hochrechnung drücken.
    var measuredDays = days.Count(d => Economics.TariffFor(d.Date, o.Tariffs) is not null);
    return new
    {
        configured = o.Tariffs.Count > 0,
        totalSavingsEur = total,
        measuredDays,
        investmentEur = o.InvestmentEur,
        paybackYears = Economics.PaybackYears(total, measuredDays, o.InvestmentEur),
        tariffs = o.Tariffs,
        months = days.GroupBy(d => d.Date[..7]).Select(g => new { month = g.Key, savingsEur = Economics.Sum(g, o.Tariffs) }).TakeLast(12),
    };
});

api.MapGet("/forecast", (ForecastService forecast, IOptions<SolarOptions> options) =>
{
    var current = forecast.Current;
    return new
    {
        configured = options.Value.Forecast.Enabled,
        fetchedTs = current?.FetchedTs,
        hours = current?.Hours,
        days = current?.Days,
    };
});

api.MapGet("/prices", (PriceService prices, IOptions<SolarOptions> options) =>
{
    var o = options.Value.DynamicPrices;
    var hours = prices.Current;
    return new
    {
        configured = o.Enabled,
        includesSurcharge = o.SurchargeCtPerKwh != 0 || o.VatPercent != 0,
        hours,
        cheapest = hours is null ? null : PriceCalculator.CheapestHours(hours, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 3),
    };
});

api.MapGet("/system", (SolarDb db, IntegrationStatus status, BackupService backup, IOptions<SolarOptions> options) =>
{
    var o = options.Value;
    var directory = Path.GetDirectoryName(Path.GetFullPath(db.Path))!;
    var backups = backup.List();
    object Item(string key, string name, bool configured) => new { key, name, configured, state = status.Get(key) };
    return new
    {
        version = typeof(Program).Assembly.GetName().Version?.ToString(3),
        uptimeSeconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - startedAt,
        memoryMb = Math.Round(Environment.WorkingSet / 1048576.0),
        dbMb = Math.Round(db.FileSizeBytes() / 1048576.0, 1),
        diskFreeGb = Math.Round(new DriveInfo(directory).AvailableFreeSpace / 1073741824.0, 1),
        backups = new { directory = backup.Directory, count = backups.Count, latest = backups.LastOrDefault().Name, keepDays = o.Backup.KeepDays },
        authEnabled = o.Auth.Enabled,
        sdCardMode = o.SdCardMode,
        pi = PiHealth.Read(),
        integrations = mirrorMode ? [Item(IntegrationStatus.Mirror, "Übertragung vom Pi", true)] : new[]
        {
            Item(IntegrationStatus.Backup, "Datensicherung", o.Backup.Enabled),
            Item(IntegrationStatus.Balcony, "Balkonkraftwerk", !string.IsNullOrWhiteSpace(o.Balcony.Host)),
            Item(IntegrationStatus.Notifications, "Benachrichtigungen", !string.IsNullOrWhiteSpace(o.Alerts.NtfyTopic) || !string.IsNullOrWhiteSpace(o.Alerts.TelegramBotToken)),
            Item(IntegrationStatus.Forecast, "Ertragsprognose", o.Forecast.Enabled),
            Item(IntegrationStatus.Prices, "Dynamische Preise", o.DynamicPrices.Enabled),
            Item(IntegrationStatus.Mqtt, "MQTT", o.Mqtt.Enabled),
            Item(IntegrationStatus.Sync, "Spiegel-Server", o.Sync.Configured),
        },
    };
});

// Der Spiegel zeigt keine Adressen und Pfade aus dem Heimnetz, auch nicht seine eigenen leeren.
api.MapGet("/settings", (SettingsStore store) => mirrorMode ? MirrorSource.ForMirror(store.Current) : store.Current.Masked());

api.MapPost("/notifications/test", async (IOptions<SolarOptions> options, IHttpClientFactory http, IntegrationStatus status, CancellationToken ct) =>
{
    var a = options.Value.Alerts;
    if (string.IsNullOrWhiteSpace(a.NtfyTopic) && (string.IsNullOrWhiteSpace(a.TelegramBotToken) || string.IsNullOrWhiteSpace(a.TelegramChatId)))
        return Results.Json(new { error = "Es ist noch kein Weg eingerichtet und gespeichert." }, statusCode: 400);
    try
    {
        if (!await AlertService.SendAsync(http.CreateClient(), a, "SKx Solar", "Testnachricht aus dem Dashboard.", Severity.Info, ct))
        {
            status.Fail(IntegrationStatus.Notifications, "Einer der Benachrichtigungswege ist ausgefallen.");
            return Results.Json(new { error = "Nur einer der beiden Wege hat funktioniert. Server, Thema und Token prüfen." }, statusCode: 502);
        }
        status.Ok(IntegrationStatus.Notifications);
        return Results.Ok();
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        // Die Fehlermeldung kann die Adresse mit dem Token enthalten, darum nur den Typ nennen.
        status.Fail(IntegrationStatus.Notifications, $"Versand fehlgeschlagen ({ex.GetType().Name}).");
        return Results.Json(new { error = "Der Versand ist fehlgeschlagen. Server, Thema und Token prüfen." }, statusCode: 502);
    }
});

api.MapGet("/advice", (SolarDb db, LiveState live, ForecastService forecast, PriceService prices, IOptions<SolarOptions> options) =>
{
    var o = options.Value;
    var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    var since = DateOnly.FromDateTime(DateTime.Now).AddDays(-14).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var hours = db.HourlyLoad(since);
    var sample = live.Current.Sample;
    var typical = hours.Where(h => h is not null).Select(h => h!.Value).DefaultIfEmpty(sample?.LoadW ?? 0).Average();
    double LoadAt(long ts) => hours[DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().Hour] ?? typical;
    var pv = forecast.Current?.Hours.ToDictionary(h => h.T, h => h.W) ?? [];

    BatteryOutlook? battery = null;
    IReadOnlyDictionary<long, double> soc = new Dictionary<long, double>();
    if (sample is not null && o.BatteryCapacityKwh is > 0 && pv.Count > 0)
    {
        // Die tiefste gemessene Entladung steht für die eingestellte Reserve des Wechselrichters.
        var reserve = Math.Clamp(db.MinSoc(since) ?? 10, 5, 50);
        (battery, soc) = Advisor.Simulate(now, sample.BatterySoc, o.BatteryCapacityKwh.Value, reserve, LoadAt, pv);
    }
    var cheapest = prices.Current is { } priceHours
        ? priceHours.Where(h => h.T + 3600 > now).OrderBy(h => h.Ct).FirstOrDefault()
        : null;
    return new
    {
        configured = pv.Count > 0,
        battery,
        window = Advisor.BestWindow(now, LoadAt, pv, soc, t => Aggregation.LocalDate(t, TimeZoneInfo.Local)),
        cheapest,
    };
});

api.MapGet("/forecast-accuracy", (SolarDb db, IOptions<SolarOptions> options) =>
{
    var today = DateOnly.FromDateTime(DateTime.Now);
    return Advisor.Accuracy(db.ForecastActuals(
        today.AddDays(-30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)), options.Value.Forecast.PerformanceRatio);
});

api.MapGet("/roof-check", (SolarDb db, IOptions<SolarOptions> options) =>
    RoofCheck.Evaluate(db.RoofMinutes(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60 * 86400L), options.Value.Forecast));

api.MapPut("/settings", (PlanningSettings settings, SettingsStore store) =>
{
    if (settings.Validate() is { } problem)
        return Results.Json(new { error = problem }, statusCode: 400);
    try
    {
        store.Save(settings);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return Results.Json(new { error = $"Speichern fehlgeschlagen: {ex.Message}" }, statusCode: 500);
    }
    return Results.Ok(store.Current.Masked());
});

api.MapGet("/export/{kind}.csv",(string kind, string? from, string? to, SolarDb db) =>
{
    if (kind is not ("minutes" or "days"))
        return Results.NotFound();
    if ((from is not null && ParseDate(from) is null) || (to is not null && ParseDate(to) is null))
        return Results.BadRequest("Ungültiges Datum.");
    var today = DateOnly.FromDateTime(DateTime.Now);
    var toDate = (ParseDate(to) ?? today).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    var fromDate = (ParseDate(from) ?? (kind == "days" ? new DateOnly(2000, 1, 1) : today))
        .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    return Results.Stream(async output =>
    {
        // SQLite liest synchron, darum erst in einen Puffer und dann asynchron zum Client.
        await using var buffer = new FileStream(Path.GetTempFileName(), FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 65536, FileOptions.DeleteOnClose);
        if (kind == "days")
            db.WriteCsv(buffer, "v_SolarDaily", "Date", fromDate, toDate);
        else
            db.WriteCsv(buffer, "v_SolarMinute", "LocalDate", fromDate, toDate);
        buffer.Position = 0;
        await buffer.CopyToAsync(output);
    }, "text/csv; charset=utf-8", $"solar-{kind}-{fromDate}_{toDate}.csv");
});

api.MapGet("/export/snapshot.db", async (SolarDb db, CancellationToken ct) =>
{
    await snapshotLock.WaitAsync(ct);
    try
    {
        var path = db.CreateSnapshot();
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, FileOptions.DeleteOnClose);
        return Results.File(stream, "application/vnd.sqlite3", $"solar-{DateTime.Now:yyyy-MM-dd}.db");
    }
    finally
    {
        snapshotLock.Release();
    }
});

app.MapGet("/health", (LiveState live) =>
{
    var sample = live.Current.Sample;
    var fresh = sample is not null && DateTimeOffset.UtcNow.ToUnixTimeSeconds() - sample.Ts < 120;
    return fresh ? Results.Ok("ok") : Results.Json(new { status = "keine aktuellen Daten", live.Current.LastError }, statusCode: 503);
});

app.Run();
return 0;

static Task SignInAsync(HttpContext context, string name)
{
    var user = context.RequestServices.GetRequiredService<IOptions<SolarOptions>>().Value.Auth.Find(name)!;
    var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.Name), new Claim(ClaimTypes.Role, user.Role), new Claim(AccountStampClaim, SettingsStore.AccountStamp(user))], CookieAuthenticationDefaults.AuthenticationScheme);
    return context.SignInAsync(new ClaimsPrincipal(identity), new AuthenticationProperties { IsPersistent = true });
}

static bool IsSelf(HttpContext context, string name) =>
    string.Equals(context.User.Identity?.Name, name, StringComparison.OrdinalIgnoreCase);

static DateOnly? ParseDate(string? value) =>
    DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

static double? Round2(double? value) => value is null ? null : Math.Round(value.Value, 2);

internal partial class Program
{
    private const string AccountStampClaim = "skx-account";
}

internal sealed record LoginRequest(string? Username, string? Password);
internal sealed record UserRequest(string? Username, string? Password, string? Role);
internal sealed record PasswordChange(string? Current, string? Next);
