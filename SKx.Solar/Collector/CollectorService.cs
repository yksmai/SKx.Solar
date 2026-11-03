using System.Text;
using Microsoft.Extensions.Options;
using SKx.Solar.Deye;
using SKx.Solar.Integrations;
using SKx.Solar.Storage;

namespace SKx.Solar.Collector;

public sealed record DeviceInfo(string SerialNumber, int DeviceType, bool HighVoltage);

public sealed class LiveState
{
    private volatile Snapshot _current = new(null, null, null, null);
    private volatile IReadOnlyList<InverterSample> _pending = [];
    private long _heartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    public sealed record Snapshot(InverterSample? Sample, DeviceInfo? Device, string? LastError, DateTimeOffset? LastErrorAt);

    public Snapshot Current => _current;

    /// <summary>Noch nicht gespeicherte Messwerte, damit Diagramme trotz seltenem Schreiben aktuell bleiben.</summary>
    public IReadOnlyList<InverterSample> Pending
    {
        get => _pending;
        set => _pending = value;
    }

    /// <summary>Letzter Durchlauf der Datensammlung, daran erkennt der Watchdog eine hängende App.</summary>
    public long Heartbeat
    {
        get => Interlocked.Read(ref _heartbeat);
        set => Interlocked.Exchange(ref _heartbeat, value);
    }

    public void SetSample(InverterSample sample) => _current = _current with { Sample = sample, LastError = null, LastErrorAt = null };
    public void SetDevice(DeviceInfo device) => _current = _current with { Device = device };
    public void SetError(string error) => _current = _current with { LastError = error, LastErrorAt = DateTimeOffset.UtcNow };
    public void ClearSample() => _current = _current with { Sample = null, LastError = null, LastErrorAt = null };
    public void Restore(Snapshot snapshot) => _current = snapshot;
}

public sealed class CollectorService(
    IOptions<SolarOptions> options,
    SolarDb db,
    LiveState live,
    ILogger<CollectorService> logger,
    UpstreamMonitor upstream,
    Func<uint, IRegisterReader>? readerFactory = null) : BackgroundService
{
    private const int MaxBufferedSamples = 8640;
    private const int SdFlushSeconds = 300;
    private const int SdRawSeconds = 60;
    private const int SdRetentionDays = 14;
    private readonly SolarOptions _options = options.Value;
    private readonly List<InverterSample> _buffer = [];
    private CounterGuard _guard = new(null);
    private long _lastCleanup;
    private long _lastFlush = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private bool _failing;
    private (string? Host, int Port, uint? LoggerSerial) _target;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _guard = new CounterGuard(db.LatestSample());
        await upstream.FirstCheck.WaitAsync(stoppingToken);
        var paused = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.PollSeconds));
        IRegisterReader? reader = null;
        DeviceInfo? device = null;
        try
        {
            do
            {
                live.Heartbeat = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // Solange der Pi Daten liefert, bleibt der Logger von dieser Instanz unbehelligt.
                if (upstream.IsReachable)
                {
                    if (!paused)
                    {
                        paused = true;
                        Flush(_buffer.ToList());
                        _buffer.Clear();
                        live.ClearSample();
                        logger.LogInformation("Pi erreichbar, direkte Abfrage pausiert.");
                    }
                    continue;
                }
                if (paused)
                {
                    paused = false;
                    logger.LogInformation("Pi nicht erreichbar, lese direkt vom Wechselrichter.");
                }

                // Eine neue Adresse aus dem Dashboard gilt ab der nächsten Abfrage.
                var target = (_options.Host, _options.Port, _options.LoggerSerial);
                if (target != _target)
                {
                    reader = null;
                    device = null;
                    _target = target;
                }
                try
                {
                    if (string.IsNullOrWhiteSpace(target.Host))
                        throw new InvalidOperationException("Der Wechselrichter ist noch nicht eingerichtet. Adresse unter Einstellungen eintragen.");
                    reader ??= await CreateReaderAsync(target.Host, target.Port, target.LoggerSerial, stoppingToken);
                    device ??= await ReadDeviceAsync(reader, stoppingToken);
                    await PollAsync(reader, device, stoppingToken);
                    if (_failing)
                        logger.LogInformation("Verbindung zum Wechselrichter wiederhergestellt.");
                    _failing = false;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    live.SetError(ex.Message);
                    // Nur beim Wechsel loggen, damit das Journal die SD-Karte nicht unnötig belastet.
                    if (!_failing)
                        logger.LogWarning("Abfrage fehlgeschlagen: {Message}", ex.Message);
                    _failing = true;
                }
                FlushCompletedMinutes(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                live.Pending = _buffer.ToArray();
                Cleanup();
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            Flush(_buffer.ToList());
        }
    }

    private async Task<IRegisterReader> CreateReaderAsync(string host, int port, uint? loggerSerial, CancellationToken ct)
    {
        var serial = loggerSerial ?? await SolarmanClient.DiscoverSerialAsync(host, ct);
        logger.LogInformation("Logger {Serial} unter {Host}:{Port}", serial, host, port);
        return readerFactory?.Invoke(serial)
            ?? new SolarmanClient(host, port, serial, TimeSpan.FromSeconds(8));
    }

    private async Task<DeviceInfo> ReadDeviceAsync(IRegisterReader reader, CancellationToken ct)
    {
        var block = (await reader.ReadAsync([(0, 8)], ct))[0];
        var device = ParseDevice(block);
        if (device.DeviceType != 6)
            logger.LogWarning("Gerätetyp {Type} ist nicht getestet, die Skalierung kann abweichen.", device.DeviceType);
        live.SetDevice(device);
        return device;
    }

    internal static DeviceInfo ParseDevice(RegisterBlock block)
    {
        var serial = new StringBuilder();
        for (var r = 3; r <= 7; r++)
            serial.Append((char)(block[r] >> 8)).Append((char)(block[r] & 0xFF));
        return new DeviceInfo(serial.ToString().Trim('\0', ' '), block[0], block[0] == 6);
    }

    private async Task PollAsync(IRegisterReader reader, DeviceInfo device, CancellationToken ct)
    {
        var blocks = await reader.ReadAsync(DeyeDecoder.Blocks, ct);
        var sample = DeyeDecoder.Decode(blocks, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), device.HighVoltage);
        if (!_guard.Accept(sample, out var rebased))
            throw new InvalidDataException("Unplausible Messwerte verworfen.");
        if (rebased)
            logger.LogWarning("Die Zählerstände passen dauerhaft nicht zum alten Stand, der neue gilt ab jetzt.");

        live.SetSample(sample);
        _buffer.Add(sample);
        if (_buffer.Count > MaxBufferedSamples)
            _buffer.RemoveAt(0);
    }

    private void FlushCompletedMinutes(long now)
    {
        if (_options.SdCardMode && now - _lastFlush < SdFlushSeconds)
            return;
        var minute = now / 60;
        var completed = _buffer.Where(s => s.Ts / 60 < minute).ToList();
        if (Flush(completed))
        {
            _buffer.RemoveAll(s => s.Ts / 60 < minute);
            _lastFlush = now;
        }
    }

    private bool Flush(List<InverterSample> samples)
    {
        try
        {
            db.Store(samples, TimeZoneInfo.Local, _options.SdCardMode ? SdRawSeconds : 0);
            return true;
        }
        catch (Exception ex)
        {
            // Puffer bleibt erhalten, der nächste Durchlauf versucht es erneut.
            logger.LogError(ex, "Speichern fehlgeschlagen.");
            return false;
        }
    }

    private void Cleanup()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - _lastCleanup < 86400)
            return;
        _lastCleanup = now;
        try
        {
            var days = _options.SdCardMode ? Math.Min(_options.RawRetentionDays, SdRetentionDays) : _options.RawRetentionDays;
            db.DeleteSamplesBefore(now - days * 86400L);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Aufräumen fehlgeschlagen.");
        }
    }
}
