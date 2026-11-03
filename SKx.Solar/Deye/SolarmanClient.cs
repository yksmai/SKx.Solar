using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SKx.Solar.Deye;

public interface IRegisterReader
{
    Task<RegisterBlock[]> ReadAsync(IReadOnlyList<(ushort Start, ushort Count)> blocks, CancellationToken ct);
}

/// <summary>
/// Solarman-V5-Client für den Datenlogger (TCP 8899). Bewusst nur lesend:
/// Es gibt ausschließlich Modbus-Funktion 03, ein Schreibbefehl lässt sich hiermit nicht absetzen.
/// </summary>
public sealed class SolarmanClient(string host, int port, uint loggerSerial, TimeSpan timeout) : IRegisterReader
{
    private ushort _sequence;

    public async Task<RegisterBlock[]> ReadAsync(IReadOnlyList<(ushort Start, ushort Count)> blocks, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        // Der Logger verkraftet lange offene Verbindungen schlecht, daher eine Verbindung je Abfragezyklus.
        using var client = new TcpClient { NoDelay = true };
        await client.ConnectAsync(host, port, timeoutCts.Token);
        var stream = client.GetStream();

        var result = new RegisterBlock[blocks.Count];
        for (var i = 0; i < blocks.Count; i++)
        {
            var (start, count) = blocks[i];
            var request = SolarmanFrame.BuildReadRequest(loggerSerial, ++_sequence, start, count);
            await stream.WriteAsync(request, timeoutCts.Token);
            result[i] = new RegisterBlock(start, await ReadResponseAsync(stream, count, _sequence, timeoutCts.Token));
            await Task.Delay(100, timeoutCts.Token);
        }
        return result;
    }

    private static async Task<ushort[]> ReadResponseAsync(NetworkStream stream, ushort expected, ushort sequence, CancellationToken ct)
    {
        // Der Logger schiebt gelegentlich eigene Frames (Heartbeat) dazwischen; die werden übersprungen.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var header = new byte[SolarmanFrame.HeaderLength];
            await stream.ReadExactlyAsync(header, ct);
            if (header[0] != SolarmanFrame.Start)
                throw new InvalidDataException("Ungültiger Frame-Anfang vom Logger.");

            var payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
            var frame = new byte[SolarmanFrame.HeaderLength + payloadLength + 2];
            header.CopyTo(frame, 0);
            await stream.ReadExactlyAsync(frame.AsMemory(SolarmanFrame.HeaderLength), ct);

            // Zwei Blöcke sind gleich lang, nur die Sequenz zeigt, dass die Antwort zu dieser Anfrage gehört.
            if (SolarmanFrame.IsReadResponse(frame) && SolarmanFrame.MatchesSequence(frame, sequence))
                return SolarmanFrame.ParseReadResponse(frame, expected);
        }
        throw new InvalidDataException("Keine passende Antwort vom Logger erhalten.");
    }

    /// <summary>Fragt die Seriennummer des Loggers per UDP ab, dafür ist keine Anmeldung nötig.</summary>
    public static async Task<uint> DiscoverSerialAsync(string host, CancellationToken ct)
    {
        using var udp = new UdpClient();
        var message = Encoding.ASCII.GetBytes("WIFIKIT-214028-READ");
        await udp.SendAsync(message, new IPEndPoint(IPAddress.Parse(host), 48899), ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
        var response = await udp.ReceiveAsync(timeoutCts.Token);
        var parts = Encoding.ASCII.GetString(response.Buffer).Split(',');
        return parts.Length >= 3 && uint.TryParse(parts[2], out var serial)
            ? serial
            : throw new InvalidDataException("Unerwartete Discovery-Antwort vom Logger.");
    }
}

public static class SolarmanFrame
{
    public const byte Start = 0xA5;
    public const byte End = 0x15;
    public const int HeaderLength = 11;
    private const ushort ControlRequest = 0x4510;
    private const ushort ControlResponse = 0x1510;
    private const int ResponsePrefixLength = 14;

    public static byte[] BuildReadRequest(uint serial, ushort sequence, ushort start, ushort count)
    {
        Span<byte> rtu = stackalloc byte[8];
        rtu[0] = 1;
        rtu[1] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(rtu[2..], start);
        BinaryPrimitives.WriteUInt16BigEndian(rtu[4..], count);
        BinaryPrimitives.WriteUInt16LittleEndian(rtu[6..], Crc(rtu[..6]));

        const int payloadLength = 15 + 8;
        var frame = new byte[HeaderLength + payloadLength + 2];
        frame[0] = Start;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(1), payloadLength);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(3), ControlRequest);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(5), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(7), serial);
        frame[HeaderLength] = 0x02;
        rtu.CopyTo(frame.AsSpan(HeaderLength + 15));
        frame[^2] = Checksum(frame.AsSpan(1, frame.Length - 3));
        frame[^1] = End;
        return frame;
    }

    public static bool IsReadResponse(ReadOnlySpan<byte> frame) =>
        frame.Length > HeaderLength && BinaryPrimitives.ReadUInt16LittleEndian(frame[3..]) == ControlResponse;

    /// <summary>Der Logger wiederholt nur das untere Byte der Sequenz, das obere setzt er selbst.</summary>
    public static bool MatchesSequence(ReadOnlySpan<byte> frame, ushort sequence) =>
        frame.Length > HeaderLength && frame[5] == (byte)sequence;

    public static ushort[] ParseReadResponse(ReadOnlySpan<byte> frame, ushort expectedCount)
    {
        if (frame[0] != Start || frame[^1] != End)
            throw new InvalidDataException("Frame-Grenzen ungültig.");
        if (frame[^2] != Checksum(frame[1..^2]))
            throw new InvalidDataException("Frame-Prüfsumme ungültig.");

        var rtu = frame[(HeaderLength + ResponsePrefixLength)..^2];
        if (rtu.Length < 5)
            throw new InvalidDataException("Logger meldet keine Modbus-Antwort, ist die Seriennummer korrekt?");
        if ((rtu[1] & 0x80) != 0)
            throw new InvalidDataException($"Modbus-Fehler {rtu[2]}.");

        var byteCount = rtu[2];
        // Manche Logger hängen zwei zusätzliche Bytes an, daher zählt nur das Byte-Count-Feld.
        if (rtu[1] != 3 || byteCount != expectedCount * 2 || rtu.Length < 5 + byteCount)
            throw new InvalidDataException("Modbus-Antwort passt nicht zur Anfrage.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(rtu[(3 + byteCount)..]) != Crc(rtu[..(3 + byteCount)]))
            throw new InvalidDataException("Modbus-CRC ungültig.");

        var values = new ushort[expectedCount];
        for (var i = 0; i < expectedCount; i++)
            values[i] = BinaryPrimitives.ReadUInt16BigEndian(rtu[(3 + i * 2)..]);
        return values;
    }

    private static byte Checksum(ReadOnlySpan<byte> data)
    {
        byte sum = 0;
        foreach (var b in data) sum += b;
        return sum;
    }

    internal static ushort Crc(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
        }
        return crc;
    }
}
