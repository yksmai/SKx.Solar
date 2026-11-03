using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;
using SKx.Solar.Deye;
using SKx.Solar.Integrations;

namespace SKx.Solar.Storage;

public sealed class MinuteRow
{
    public long Ts { get; set; }
    public string LocalTime { get; set; } = "";
    public string LocalDate { get; set; } = "";
    public double PvW { get; set; }
    public double LoadW { get; set; }
    public double GridW { get; set; }
    public double ImportW { get; set; }
    public double ExportW { get; set; }
    public double BatteryW { get; set; }
    public double ChargeW { get; set; }
    public double DischargeW { get; set; }
    public double BatterySoc { get; set; }
    public double BatteryV { get; set; }
    public double BatteryTempC { get; set; }
    public double InverterTempC { get; set; }
    public double GridV { get; set; }
    public long SampleCount { get; set; }
    public double BalconyW { get; set; }
}

public sealed class DayEnergy
{
    public string Date { get; set; } = "";
    public double PvKwh { get; set; }
    public double LoadKwh { get; set; }
    public double ImportKwh { get; set; }
    public double ExportKwh { get; set; }
    public double ChargeKwh { get; set; }
    public double DischargeKwh { get; set; }
    public double? AutarkyPct { get; set; }
    public double? SelfConsumptionPct { get; set; }
    public double? PeakPvW { get; set; }
    public double? MinSoc { get; set; }
    public double? MaxSoc { get; set; }
    /// <summary>Bereits in PvKwh und LoadKwh enthalten.</summary>
    public double BalconyKwh { get; set; }
    /// <summary>Was der Wechselrichter selbst verbraucht hat: die Lücke zwischen allem, was hinein- und hinausging.</summary>
    public double OwnUseKwh { get; set; }
}

public sealed class RoofMinute
{
    public long Ts { get; set; }
    public string LocalDate { get; set; } = "";
    public double PvW { get; set; }
    public double BatterySoc { get; set; }
}

public sealed class PeriodEnergy
{
    public string Period { get; set; } = "";
    /// <summary>Bereits in PvKwh und LoadKwh enthalten.</summary>
    public double BalconyKwh { get; set; }
    public double PvKwh { get; set; }
    public double LoadKwh { get; set; }
    public double ImportKwh { get; set; }
    public double ExportKwh { get; set; }
    public double ChargeKwh { get; set; }
    public double DischargeKwh { get; set; }
    public double? AutarkyPct { get; set; }
    public double? SelfConsumptionPct { get; set; }
}

public sealed class DayCounters
{
    public string Date { get; set; } = "";
    public double PvStart { get; set; }
    public double LoadStart { get; set; }
    public double ImportStart { get; set; }
    public double ExportStart { get; set; }
    public double ChargeStart { get; set; }
    public double DischargeStart { get; set; }
    public double PvEnd { get; set; }
    public double LoadEnd { get; set; }
    public double ImportEnd { get; set; }
    public double ExportEnd { get; set; }
    public double ChargeEnd { get; set; }
    public double DischargeEnd { get; set; }
}

/// <summary>
/// SQLite-Speicher. Rohwerte werden nach einiger Zeit gelöscht, Minuten- und Tageswerte bleiben dauerhaft.
/// Die Views v_Solar* sind die stabile Schnittstelle für Analysen, etwa als Dataset im SKx-Dashboard-Portal.
/// </summary>
public sealed class SolarDb(string path)
{
    private static readonly string[] SampleColumns =
        typeof(InverterSample).GetProperties().Select(p => p.Name).Where(n => n != nameof(InverterSample.FaultCodes)).ToArray();

    private readonly string _connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString();

    public string Path => path;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        // Gilt je Verbindung, nicht für die Datei. Schont die SD-Karte, weil nicht jeder Commit bis auf die Karte muss.
        connection.Execute("PRAGMA synchronous=NORMAL;");
        return connection;
    }

    public void Initialize()
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var sampleColumns = string.Join(",\n  ",
            SampleColumns.Where(c => c != nameof(InverterSample.Ts)).Select(c => $"{c} REAL NOT NULL"));

        using var db = Open();
        db.Execute("PRAGMA journal_mode=WAL;");
        db.Execute($"""
            CREATE TABLE IF NOT EXISTS Samples (
              Ts INTEGER PRIMARY KEY,
              {sampleColumns}
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS Minutes (
              Ts INTEGER PRIMARY KEY,
              LocalTime TEXT NOT NULL,
              LocalDate TEXT NOT NULL,
              PvW REAL NOT NULL,
              LoadW REAL NOT NULL,
              GridW REAL NOT NULL,
              ImportW REAL NOT NULL,
              ExportW REAL NOT NULL,
              BatteryW REAL NOT NULL,
              ChargeW REAL NOT NULL,
              DischargeW REAL NOT NULL,
              BatterySoc REAL NOT NULL,
              BatteryV REAL NOT NULL,
              BatteryTempC REAL NOT NULL,
              InverterTempC REAL NOT NULL,
              GridV REAL NOT NULL,
              SampleCount INTEGER NOT NULL
            ) WITHOUT ROWID;
            CREATE INDEX IF NOT EXISTS IX_Minutes_LocalDate ON Minutes(LocalDate);

            CREATE TABLE IF NOT EXISTS Days (
              Date TEXT PRIMARY KEY,
              PvStart REAL NOT NULL, LoadStart REAL NOT NULL, ImportStart REAL NOT NULL,
              ExportStart REAL NOT NULL, ChargeStart REAL NOT NULL, DischargeStart REAL NOT NULL,
              PvEnd REAL NOT NULL, LoadEnd REAL NOT NULL, ImportEnd REAL NOT NULL,
              ExportEnd REAL NOT NULL, ChargeEnd REAL NOT NULL, DischargeEnd REAL NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS Alerts (
              Id INTEGER PRIMARY KEY AUTOINCREMENT,
              Key TEXT NOT NULL,
              Severity TEXT NOT NULL,
              Title TEXT NOT NULL,
              Message TEXT NOT NULL,
              StartedTs INTEGER NOT NULL,
              EndedTs INTEGER,
              NotifiedTs INTEGER
            );
            CREATE INDEX IF NOT EXISTS IX_Alerts_StartedTs ON Alerts(StartedTs);

            CREATE TABLE IF NOT EXISTS ForecastDays (
              Date TEXT PRIMARY KEY,
              NormKwh REAL NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS BalconyMinutes (
              Ts INTEGER PRIMARY KEY,
              W REAL NOT NULL
            ) WITHOUT ROWID;

            CREATE TABLE IF NOT EXISTS BalconyDays (
              Date TEXT PRIMARY KEY,
              StartKwh REAL NOT NULL,
              EndKwh REAL NOT NULL
            ) WITHOUT ROWID;

            -- Der Deye sieht das Balkonkraftwerk nicht als Erzeugung: Es senkt nur seinen Netzbezug oder erhöht die
            -- Einspeisung. Darum zählt es hier zu Erzeugung und Verbrauch, Netz und Batterie bleiben wie gemessen.
            DROP VIEW IF EXISTS v_SolarMinute;
            CREATE VIEW v_SolarMinute AS
            SELECT LocalTime, LocalDate, ROUND(PvW + COALESCE(b.W, 0)) AS PvW, ROUND(LoadW + COALESCE(b.W, 0)) AS LoadW, ROUND(ImportW) AS ImportW,
                   ROUND(ExportW) AS ExportW, ROUND(ChargeW) AS ChargeW, ROUND(DischargeW) AS DischargeW,
                   BatterySoc, ROUND(BatteryV, 1) AS BatteryV, ROUND(BatteryTempC, 1) AS BatteryTempC,
                   ROUND(InverterTempC, 1) AS InverterTempC, ROUND(GridV, 1) AS GridV, SampleCount, ROUND(COALESCE(b.W, 0)) AS BalconyW
            FROM Minutes m LEFT JOIN BalconyMinutes b ON b.Ts = m.Ts;

            DROP VIEW IF EXISTS v_SolarDaily;
            CREATE VIEW v_SolarDaily AS
            WITH e AS (
              SELECT d.Date, MAX(PvEnd - PvStart, 0) + COALESCE(MAX(b.EndKwh - b.StartKwh, 0), 0) AS Pv,
                     MAX(LoadEnd - LoadStart, 0) + COALESCE(MAX(b.EndKwh - b.StartKwh, 0), 0) AS Load,
                     MAX(ImportEnd - ImportStart, 0) AS Import, MAX(ExportEnd - ExportStart, 0) AS Export,
                     MAX(ChargeEnd - ChargeStart, 0) AS Charge, MAX(DischargeEnd - DischargeStart, 0) AS Discharge,
                     COALESCE(MAX(b.EndKwh - b.StartKwh, 0), 0) AS Balcony
              FROM Days d LEFT JOIN BalconyDays b ON b.Date = d.Date
            )
            SELECT e.Date, ROUND(Pv, 1) AS PvKwh, ROUND(Load, 1) AS LoadKwh, ROUND(Import, 1) AS ImportKwh,
                   ROUND(Export, 1) AS ExportKwh, ROUND(Charge, 1) AS ChargeKwh, ROUND(Discharge, 1) AS DischargeKwh,
                   -- Der Netzbezug deckt auch den Eigenverbrauch des Wechselrichters, darum zählt der zum Bedarf.
                   CASE WHEN Load > 0 THEN ROUND(MAX(100.0 * (1 - Import / (Load + MAX(Pv + Import + Discharge - Load - Export - Charge, 0))), 0), 1) END AS AutarkyPct,
                   CASE WHEN Pv > 0 THEN ROUND(MAX(100.0 * (1 - Export / Pv), 0), 1) END AS SelfConsumptionPct,
                   -- Je Tag über den Index, damit eine Abfrage für heute nicht die ganze Historie liest.
                   (SELECT ROUND(MAX(m.PvW + COALESCE(b.W, 0))) FROM Minutes m LEFT JOIN BalconyMinutes b ON b.Ts = m.Ts WHERE m.LocalDate = e.Date) AS PeakPvW,
                   (SELECT MIN(BatterySoc) FROM Minutes WHERE LocalDate = e.Date) AS MinSoc,
                   (SELECT MAX(BatterySoc) FROM Minutes WHERE LocalDate = e.Date) AS MaxSoc,
                   ROUND(Balcony, 1) AS BalconyKwh,
                   ROUND(MAX(Pv + Import + Discharge - Load - Export - Charge, 0), 1) AS OwnUseKwh
            FROM e;

            DROP VIEW IF EXISTS v_SolarMonthly;
            CREATE VIEW v_SolarMonthly AS
            SELECT SUBSTR(Date, 1, 7) AS Period, ROUND(SUM(BalconyKwh), 1) AS BalconyKwh, ROUND(SUM(PvKwh), 1) AS PvKwh, ROUND(SUM(LoadKwh), 1) AS LoadKwh,
                   ROUND(SUM(ImportKwh), 1) AS ImportKwh, ROUND(SUM(ExportKwh), 1) AS ExportKwh,
                   ROUND(SUM(ChargeKwh), 1) AS ChargeKwh, ROUND(SUM(DischargeKwh), 1) AS DischargeKwh,
                   CASE WHEN SUM(LoadKwh) > 0 THEN ROUND(MAX(100.0 * (1 - SUM(ImportKwh) / (SUM(LoadKwh) + SUM(OwnUseKwh))), 0), 1) END AS AutarkyPct,
                   CASE WHEN SUM(PvKwh) > 0 THEN ROUND(MAX(100.0 * (1 - SUM(ExportKwh) / SUM(PvKwh)), 0), 1) END AS SelfConsumptionPct
            FROM v_SolarDaily GROUP BY SUBSTR(Date, 1, 7);
            """);
    }

    /// <summary>Schreibt einen Puffer aus Messpunkten atomar: Rohwerte, Minuten und Tageszähler.</summary>
    /// <param name="rawEverySeconds">Größer 0: nur den letzten Rohwert je Intervall behalten, Minuten nutzen weiter alle Werte.</param>
    public void Store(IReadOnlyList<InverterSample> samples, TimeZoneInfo zone, int rawEverySeconds = 0)
    {
        if (samples.Count == 0)
            return;

        using var db = Open();
        using var tx = db.BeginTransaction();

        var columns = string.Join(", ", SampleColumns);
        var values = string.Join(", ", SampleColumns.Select(c => "@" + c));
        db.Execute($"INSERT OR REPLACE INTO Samples ({columns}) VALUES ({values})",
            rawEverySeconds > 0 ? samples.GroupBy(s => s.Ts / rawEverySeconds).Select(g => g.MaxBy(s => s.Ts)!) : samples, tx);

        db.Execute("""
            INSERT OR REPLACE INTO Minutes (Ts, LocalTime, LocalDate, PvW, LoadW, GridW, ImportW, ExportW, BatteryW,
              ChargeW, DischargeW, BatterySoc, BatteryV, BatteryTempC, InverterTempC, GridV, SampleCount)
            VALUES (@Ts, @LocalTime, @LocalDate, @PvW, @LoadW, @GridW, @ImportW, @ExportW, @BatteryW,
              @ChargeW, @DischargeW, @BatterySoc, @BatteryV, @BatteryTempC, @InverterTempC, @GridV, @SampleCount)
            """, Aggregation.ToMinutes(samples, zone), tx);

        foreach (var day in samples.GroupBy(s => Aggregation.LocalDate(s.Ts, zone)))
        {
            var last = day.MaxBy(s => s.Ts)!;
            var existing = db.QuerySingleOrDefault<DayCounters>(
                "SELECT * FROM Days WHERE Date = @Date", new { Date = day.Key }, tx);
            var previous = existing ?? db.QuerySingleOrDefault<DayCounters>(
                "SELECT * FROM Days WHERE Date < @Date ORDER BY Date DESC LIMIT 1", new { Date = day.Key }, tx);
            db.Execute("""
                INSERT OR REPLACE INTO Days (Date, PvStart, LoadStart, ImportStart, ExportStart, ChargeStart, DischargeStart,
                  PvEnd, LoadEnd, ImportEnd, ExportEnd, ChargeEnd, DischargeEnd)
                VALUES (@Date, @PvStart, @LoadStart, @ImportStart, @ExportStart, @ChargeStart, @DischargeStart,
                  @PvEnd, @LoadEnd, @ImportEnd, @ExportEnd, @ChargeEnd, @DischargeEnd)
                """, Aggregation.NextCounters(day.Key, last, existing, previous), tx);
        }

        tx.Commit();
    }

    /// <summary>Schreibt Minutenmittel und Tageszähler. Wie beim Deye zählt Energie aus einer Lücke zum Tag der Rückkehr.</summary>
    public void StoreBalcony(IReadOnlyList<BalconyPoint> points, TimeZoneInfo zone)
    {
        if (points.Count == 0)
            return;

        using var db = Open();
        using var tx = db.BeginTransaction();
        db.Execute("INSERT OR REPLACE INTO BalconyMinutes (Ts, W) VALUES (@Ts, @W)",
            points.GroupBy(p => p.Ts / 60).Select(g => new { Ts = g.Key * 60, W = g.Average(p => p.W) }), tx);
        foreach (var day in points.GroupBy(p => Aggregation.LocalDate(p.Ts, zone)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var last = day.MaxBy(p => p.Ts)!;
            var start = db.ExecuteScalar<double?>("SELECT StartKwh FROM BalconyDays WHERE Date = @Date", new { Date = day.Key }, tx);
            var previous = db.ExecuteScalar<double?>("SELECT EndKwh FROM BalconyDays WHERE Date < @Date ORDER BY Date DESC LIMIT 1", new { Date = day.Key }, tx);
            // Ein getauschtes Gerät beginnt wieder bei null, das darf den Tag nicht ins Minus ziehen.
            var startKwh = start ?? (previous is { } end ? Math.Min(end, last.TotalKwh) : Math.Max(last.TotalKwh - last.TodayKwh, 0));
            db.Execute("INSERT OR REPLACE INTO BalconyDays (Date, StartKwh, EndKwh) VALUES (@Date, @startKwh, @EndKwh)",
                new { Date = day.Key, startKwh, EndKwh = last.TotalKwh }, tx);
        }
        tx.Commit();
    }

    public int DeleteSamplesBefore(long ts)
    {
        using var db = Open();
        return db.Execute("DELETE FROM Samples WHERE Ts < @ts", new { ts });
    }

    public InverterSample? LatestSample()
    {
        using var db = Open();
        return db.QuerySingleOrDefault<InverterSample>("SELECT * FROM Samples ORDER BY Ts DESC LIMIT 1");
    }

    public IReadOnlyList<MinuteRow> Minutes(string date)
    {
        using var db = Open();
        return db.Query<MinuteRow>("""
            SELECT m.*, COALESCE(b.W, 0.0) AS BalconyW FROM Minutes m LEFT JOIN BalconyMinutes b ON b.Ts = m.Ts
            WHERE m.LocalDate = @date ORDER BY m.Ts
            """, new { date }).AsList();
    }

    public DayEnergy? Day(string date)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<DayEnergy>("SELECT * FROM v_SolarDaily WHERE Date = @date", new { date });
    }

    public IReadOnlyList<DayEnergy> Days(string fromDate, string toDate)
    {
        using var db = Open();
        return db.Query<DayEnergy>("SELECT * FROM v_SolarDaily WHERE Date BETWEEN @fromDate AND @toDate ORDER BY Date",
            new { fromDate, toDate }).AsList();
    }

    public IReadOnlyList<PeriodEnergy> Months(string year)
    {
        using var db = Open();
        return db.Query<PeriodEnergy>("SELECT * FROM v_SolarMonthly WHERE Period LIKE @prefix ORDER BY Period",
            new { prefix = year + "-%" }).AsList();
    }

    /// <summary>Teilt jede Minute auf wie die Live-Anzeige: Das Haus nimmt zuerst Solarstrom, dann Batterie, dann Netz.</summary>
    public MinuteFlows? DayMinuteFlows(string date)
    {
        using var db = Open();
        return db.QuerySingleOrDefault<MinuteFlows>("""
            WITH a AS (
              SELECT MAX(m.PvW + COALESCE(b.W, 0), 0) AS pv, MAX(m.LoadW + COALESCE(b.W, 0), 0) AS load,
                     m.ImportW AS imp, m.ChargeW AS ch, m.DischargeW AS dis
              FROM Minutes m LEFT JOIN BalconyMinutes b ON b.Ts = m.Ts WHERE m.LocalDate = @date
            ),
            c AS (SELECT *, MIN(pv, load) AS pvHome FROM a),
            d AS (SELECT *, MIN(dis, load - pvHome) AS batHome FROM c),
            e AS (SELECT *, MIN(imp, load - pvHome - batHome) AS gridHome, MIN(pv - pvHome, ch) AS pvBat FROM d)
            SELECT SUM(imp) / 60000 AS Import, SUM(dis) / 60000 AS Discharge, SUM(ch) / 60000 AS Charge,
                   SUM(gridHome) / 60000 AS GridHome, SUM(batHome) / 60000 AS BatteryHome,
                   SUM(MIN(ch - pvBat, imp - gridHome)) / 60000 AS GridBattery
            FROM e HAVING COUNT(*) > 0
            """, new { date });
    }

    public void WriteCsv(Stream output, string view, string dateColumn, string fromDate, string toDate)
    {
        if (view is not ("v_SolarMinute" or "v_SolarDaily") || dateColumn is not ("LocalDate" or "Date"))
            throw new ArgumentException("Unbekannte Datenquelle.");

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT * FROM {view} WHERE {dateColumn} BETWEEN @fromDate AND @toDate";
        command.Parameters.AddWithValue("@fromDate", fromDate);
        command.Parameters.AddWithValue("@toDate", toDate);
        using var reader = command.ExecuteReader();

        // Für Excel in deutscher Einstellung: BOM, Semikolon und Dezimalkomma.
        using var writer = new StreamWriter(output, new System.Text.UTF8Encoding(true), leaveOpen: true);
        writer.WriteLine(string.Join(';', Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)));
        while (reader.Read())
        {
            writer.WriteLine(string.Join(';', Enumerable.Range(0, reader.FieldCount).Select(i =>
                reader.IsDBNull(i) ? "" : reader.GetValue(i) switch
                {
                    double d => d.ToString("0.###", NumberFormat),
                    var other => Convert.ToString(other, CultureInfo.InvariantCulture),
                })));
        }
    }

    private static readonly NumberFormatInfo NumberFormat = new() { NumberDecimalSeparator = "," };

    /// <summary>Erzeugt eine konsistente Kopie der Datenbank, die SKx direkt als SQLite-Quelle öffnen kann.</summary>
    public string CreateSnapshot(string? targetPath = null)
    {
        var target = targetPath ?? System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!, $"snapshot-{Guid.NewGuid():N}.db");
        using var db = Open();
        db.Execute("VACUUM INTO @target", new { target });
        return target;
    }

    public AlertRow OpenAlert(AlertRow alert)
    {
        using var db = Open();
        alert.Id = db.ExecuteScalar<long>("""
            INSERT INTO Alerts (Key, Severity, Title, Message, StartedTs) VALUES (@Key, @Severity, @Title, @Message, @StartedTs);
            SELECT last_insert_rowid();
            """, alert);
        return alert;
    }

    public void CloseAlert(long id, long endedTs)
    {
        using var db = Open();
        db.Execute("UPDATE Alerts SET EndedTs = @endedTs WHERE Id = @id", new { id, endedTs });
    }

    public void MarkAlertNotified(long id, long ts)
    {
        using var db = Open();
        db.Execute("UPDATE Alerts SET NotifiedTs = @ts WHERE Id = @id", new { id, ts });
    }

    public IReadOnlyList<AlertRow> OpenAlerts()
    {
        using var db = Open();
        return db.Query<AlertRow>("SELECT * FROM Alerts WHERE EndedTs IS NULL ORDER BY StartedTs DESC").AsList();
    }

    public IReadOnlyList<AlertRow> RecentAlerts(int limit)
    {
        using var db = Open();
        return db.Query<AlertRow>("SELECT * FROM Alerts ORDER BY EndedTs IS NOT NULL, StartedTs DESC LIMIT @limit", new { limit }).AsList();
    }

    public IReadOnlyList<DayEnergy> AllDays()
    {
        using var db = Open();
        return db.Query<DayEnergy>("SELECT * FROM v_SolarDaily ORDER BY Date").AsList();
    }

    public IReadOnlyList<DayEnergy> BestDays(int limit)
    {
        using var db = Open();
        return db.Query<DayEnergy>("SELECT * FROM v_SolarDaily ORDER BY PvKwh DESC LIMIT @limit", new { limit }).AsList();
    }

    /// <summary>Anteil der Minuten je Ladestand-Bereich (0–9, 10–19, … 90–100 %) seit einem Tag.</summary>
    public IReadOnlyList<long> SocHistogram(string fromDate)
    {
        using var db = Open();
        var bins = new long[10];
        foreach (var row in db.Query<(long Bin, long Count)>("""
            SELECT CAST(MIN(BatterySoc, 99) / 10 AS INTEGER) AS Bin, COUNT(*) AS Count
            FROM Minutes WHERE LocalDate >= @fromDate GROUP BY 1
            """, new { fromDate }))
            bins[Math.Clamp(row.Bin, 0, 9)] = row.Count;
        return bins;
    }

    public (double? Min, double? Max) BatteryTempRange(string fromDate)
    {
        using var db = Open();
        return db.QuerySingle<(double? Min, double? Max)>(
            "SELECT MIN(BatteryTempC), MAX(BatteryTempC) FROM Minutes WHERE LocalDate >= @fromDate", new { fromDate });
    }

    /// <summary>Nur das Dach: Die Balkonwerte stehen in einer eigenen Tabelle.</summary>
    public IReadOnlyList<RoofMinute> RoofMinutes(long sinceTs)
    {
        using var db = Open();
        return db.Query<RoofMinute>(
            "SELECT Ts, LocalDate, PvW, BatterySoc FROM Minutes WHERE Ts >= @sinceTs AND PvW > 20 ORDER BY Ts", new { sinceTs }).ToList();
    }

    /// <summary>Mittlere Leistung beider Strings, solange sie produzieren, aus den Rohwerten.</summary>
    public (double? Pv1W, double? Pv2W) StringAverages(long sinceTs)
    {
        using var db = Open();
        return db.QuerySingle<(double? Pv1W, double? Pv2W)>(
            "SELECT AVG(Pv1W), AVG(Pv2W) FROM Samples WHERE Ts >= @sinceTs AND PvW > 50", new { sinceTs });
    }

    /// <summary>Mittlerer Verbrauch je Stunde des Tages, Index 0 bis 23. Stunden ohne Daten bleiben null.</summary>
    public double?[] HourlyLoad(string fromDate)
    {
        using var db = Open();
        var hours = new double?[24];
        foreach (var row in db.Query<(long Hour, double Load)>("""
            SELECT CAST(SUBSTR(LocalTime, 12, 2) AS INTEGER) AS Hour, AVG(LoadW) AS Load
            FROM Minutes WHERE LocalDate >= @fromDate GROUP BY 1
            """, new { fromDate }))
            hours[Math.Clamp(row.Hour, 0, 23)] = row.Load;
        return hours;
    }

    public double? MinSoc(string fromDate)
    {
        using var db = Open();
        return db.ExecuteScalar<double?>("SELECT MIN(BatterySoc) FROM Minutes WHERE LocalDate >= @fromDate", new { fromDate });
    }

    /// <summary>Für heute gilt die Vorhersage vom Vortag, damit der Vergleich nicht die Tageswerte von heute abschreibt.</summary>
    public void SaveForecast(string today, IEnumerable<(string Date, double NormKwh)> days)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        foreach (var (date, normKwh) in days)
        {
            var verb = string.CompareOrdinal(date, today) <= 0 ? "INSERT OR IGNORE" : "INSERT OR REPLACE";
            db.Execute($"{verb} INTO ForecastDays (Date, NormKwh) VALUES (@date, @normKwh)", new { date, normKwh }, tx);
        }
        tx.Commit();
    }

    /// <summary>Die Prognose rechnet nur mit den Dachflächen am Deye, darum zählt das Balkonkraftwerk hier nicht mit.</summary>
    public IReadOnlyList<ForecastActual> ForecastActuals(string fromDate, string beforeDate)
    {
        using var db = Open();
        return db.Query<ForecastActual>("""
            SELECT f.Date, f.NormKwh, d.PvKwh - d.BalconyKwh AS PvKwh, d.MaxSoc FROM ForecastDays f JOIN v_SolarDaily d ON d.Date = f.Date
            WHERE f.Date >= @fromDate AND f.Date < @beforeDate ORDER BY f.Date
            """, new { fromDate, beforeDate }).AsList();
    }

    public List<MinuteRow> MinutesSince(long ts, int limit)
    {
        using var db = Open();
        return db.Query<MinuteRow>("SELECT * FROM Minutes WHERE Ts >= @ts ORDER BY Ts LIMIT @limit", new { ts, limit }).AsList();
    }

    public List<BalconyMinuteRow> BalconyMinutesSince(long ts, int limit)
    {
        using var db = Open();
        return db.Query<BalconyMinuteRow>("SELECT * FROM BalconyMinutes WHERE Ts >= @ts ORDER BY Ts LIMIT @limit", new { ts, limit }).AsList();
    }

    public List<DayCounters> DayCountersSince(string date)
    {
        using var db = Open();
        return db.Query<DayCounters>("SELECT * FROM Days WHERE Date >= @date ORDER BY Date", new { date }).AsList();
    }

    public List<BalconyDayRow> BalconyDaysSince(string date)
    {
        using var db = Open();
        return db.Query<BalconyDayRow>("SELECT * FROM BalconyDays WHERE Date >= @date ORDER BY Date", new { date }).AsList();
    }

    public List<ForecastDayRow> ForecastDaysSince(string date)
    {
        using var db = Open();
        return db.Query<ForecastDayRow>("SELECT * FROM ForecastDays WHERE Date >= @date ORDER BY Date", new { date }).AsList();
    }

    /// <summary>Doppelt gesendete Zeilen ersetzen die alten, Meldungen kommen immer als ganze Liste.</summary>
    public void StoreMirror(MirrorPush push)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        db.Execute("""
            INSERT OR REPLACE INTO Minutes (Ts, LocalTime, LocalDate, PvW, LoadW, GridW, ImportW, ExportW, BatteryW,
              ChargeW, DischargeW, BatterySoc, BatteryV, BatteryTempC, InverterTempC, GridV, SampleCount)
            VALUES (@Ts, @LocalTime, @LocalDate, @PvW, @LoadW, @GridW, @ImportW, @ExportW, @BatteryW,
              @ChargeW, @DischargeW, @BatterySoc, @BatteryV, @BatteryTempC, @InverterTempC, @GridV, @SampleCount)
            """, push.Minutes, tx);
        db.Execute("INSERT OR REPLACE INTO BalconyMinutes (Ts, W) VALUES (@Ts, @W)", push.BalconyMinutes, tx);
        db.Execute("""
            INSERT OR REPLACE INTO Days (Date, PvStart, LoadStart, ImportStart, ExportStart, ChargeStart, DischargeStart,
              PvEnd, LoadEnd, ImportEnd, ExportEnd, ChargeEnd, DischargeEnd)
            VALUES (@Date, @PvStart, @LoadStart, @ImportStart, @ExportStart, @ChargeStart, @DischargeStart,
              @PvEnd, @LoadEnd, @ImportEnd, @ExportEnd, @ChargeEnd, @DischargeEnd)
            """, push.Days, tx);
        db.Execute("INSERT OR REPLACE INTO BalconyDays (Date, StartKwh, EndKwh) VALUES (@Date, @StartKwh, @EndKwh)", push.BalconyDays, tx);
        if (push.Planning is { } planning)
        {
            db.Execute("INSERT OR REPLACE INTO ForecastDays (Date, NormKwh) VALUES (@Date, @NormKwh)", planning.ForecastDays, tx);
            db.Execute("DELETE FROM Alerts", transaction: tx);
            db.Execute("""
                INSERT INTO Alerts (Id, Key, Severity, Title, Message, StartedTs, EndedTs, NotifiedTs)
                VALUES (@Id, @Key, @Severity, @Title, @Message, @StartedTs, @EndedTs, @NotifiedTs)
                """, planning.Alerts, tx);
        }
        tx.Commit();
    }

    public MirrorAck MirrorCursor(bool needsPlanning)
    {
        using var db = Open();
        return db.QuerySingle<(long MinuteTs, long BalconyTs, string DayDate, string BalconyDayDate)>("""
            SELECT COALESCE((SELECT MAX(Ts) FROM Minutes), 0), COALESCE((SELECT MAX(Ts) FROM BalconyMinutes), 0),
                   COALESCE((SELECT MAX(Date) FROM Days), ''), COALESCE((SELECT MAX(Date) FROM BalconyDays), '')
            """) is var c
            ? new MirrorAck(c.MinuteTs, c.BalconyTs, c.DayDate, c.BalconyDayDate, needsPlanning)
            : throw new InvalidOperationException();
    }

    public long FileSizeBytes()
    {
        var file = new FileInfo(path);
        var wal = new FileInfo(path + "-wal");
        return (file.Exists ? file.Length : 0) + (wal.Exists ? wal.Length : 0);
    }
}

public sealed class ForecastActual
{
    public string Date { get; set; } = "";
    public double NormKwh { get; set; }
    public double PvKwh { get; set; }
    public double? MaxSoc { get; set; }
}

public sealed class AlertRow
{
    public long Id { get; set; }
    public string Key { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public long StartedTs { get; set; }
    public long? EndedTs { get; set; }
    public long? NotifiedTs { get; set; }
}

public static class Aggregation
{
    public static string LocalDate(long ts, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), zone).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static IEnumerable<MinuteRow> ToMinutes(IEnumerable<InverterSample> samples, TimeZoneInfo zone)
    {
        foreach (var group in samples.GroupBy(s => s.Ts / 60).OrderBy(g => g.Key))
        {
            var ts = group.Key * 60;
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), zone);
            var last = group.MaxBy(s => s.Ts)!;
            yield return new MinuteRow
            {
                Ts = ts,
                LocalTime = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                LocalDate = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                PvW = group.Average(s => s.PvW),
                LoadW = group.Average(s => s.LoadW),
                GridW = group.Average(s => s.GridW),
                ImportW = group.Average(s => Math.Max(s.GridW, 0)),
                ExportW = group.Average(s => Math.Max(-s.GridW, 0)),
                BatteryW = group.Average(s => s.BatteryW),
                ChargeW = group.Average(s => Math.Max(-s.BatteryW, 0)),
                DischargeW = group.Average(s => Math.Max(s.BatteryW, 0)),
                BatterySoc = last.BatterySoc,
                BatteryV = group.Average(s => s.BatteryV),
                BatteryTempC = group.Average(s => s.BatteryTempC),
                InverterTempC = group.Average(s => s.InverterTempC),
                GridV = group.Average(s => (s.GridV1 + s.GridV2 + s.GridV3) / 3),
                SampleCount = group.Count(),
            };
        }
    }

    /// <summary>
    /// Tageswerte entstehen aus den Gesamtzählern. So zählt Energie aus einem Ausfall des Pi
    /// zum Tag, an dem er zurückkommt, und die Uhr des Wechselrichters spielt keine Rolle.
    /// </summary>
    public static DayCounters NextCounters(string date, InverterSample last, DayCounters? existing, DayCounters? previous)
    {
        // Läuft ein Zähler rückwärts (Gerätetausch), bleibt die schon gezählte Energie des Tages erhalten.
        static double Keep(double start, double end, double now) => now < end ? now - (end - start) : start;
        static double Next(double end, double now) => Math.Min(end, now);

        var start = existing is not null
            ? (Keep(existing.PvStart, existing.PvEnd, last.TotalPvKwh), Keep(existing.LoadStart, existing.LoadEnd, last.TotalLoadKwh),
               Keep(existing.ImportStart, existing.ImportEnd, last.TotalImportKwh), Keep(existing.ExportStart, existing.ExportEnd, last.TotalExportKwh),
               Keep(existing.ChargeStart, existing.ChargeEnd, last.TotalChargeKwh), Keep(existing.DischargeStart, existing.DischargeEnd, last.TotalDischargeKwh))
            : previous is not null
                ? (Next(previous.PvEnd, last.TotalPvKwh), Next(previous.LoadEnd, last.TotalLoadKwh), Next(previous.ImportEnd, last.TotalImportKwh),
                   Next(previous.ExportEnd, last.TotalExportKwh), Next(previous.ChargeEnd, last.TotalChargeKwh), Next(previous.DischargeEnd, last.TotalDischargeKwh))
                : (Math.Max(last.TotalPvKwh - last.TodayPvKwh, 0),
                   Math.Max(last.TotalLoadKwh - last.TodayLoadKwh, 0),
                   Math.Max(last.TotalImportKwh - last.TodayImportKwh, 0),
                   Math.Max(last.TotalExportKwh - last.TodayExportKwh, 0),
                   Math.Max(last.TotalChargeKwh - last.TodayChargeKwh, 0),
                   Math.Max(last.TotalDischargeKwh - last.TodayDischargeKwh, 0));

        return new DayCounters
        {
            Date = date,
            PvStart = start.Item1,
            LoadStart = start.Item2,
            ImportStart = start.Item3,
            ExportStart = start.Item4,
            ChargeStart = start.Item5,
            DischargeStart = start.Item6,
            PvEnd = last.TotalPvKwh,
            LoadEnd = last.TotalLoadKwh,
            ImportEnd = last.TotalImportKwh,
            ExportEnd = last.TotalExportKwh,
            ChargeEnd = last.TotalChargeKwh,
            DischargeEnd = last.TotalDischargeKwh,
        };
    }
}
