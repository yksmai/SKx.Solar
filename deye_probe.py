"""Liest Register eines Deye-Wechselrichters ueber den Solarman-V5-Port 8899 (nur lesend, Funktion 03)."""
import socket
import struct
import sys
import time

if len(sys.argv) < 3:
    sys.exit("Aufruf: python deye_probe.py <Logger-IP> <Logger-Seriennummer> [Port]")
HOST = sys.argv[1]
SERIAL = int(sys.argv[2])
PORT = int(sys.argv[3]) if len(sys.argv) > 3 else 8899


def modbus_crc(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return struct.pack("<H", crc)


def build_frame(start, count, seq):
    rtu = struct.pack(">BBHH", 1, 3, start, count)
    rtu += modbus_crc(rtu)
    payload = b"\x02" + b"\x00" * 14 + rtu
    head = b"\xa5" + struct.pack("<HHHI", len(payload), 0x4510, seq, SERIAL)
    body = head + payload
    return body + bytes([sum(body[1:]) & 0xFF]) + b"\x15"


def read_registers(sock, start, count, seq):
    sock.sendall(build_frame(start, count, seq))
    resp = sock.recv(1024)
    rtu = resp[25:-2]
    if len(rtu) < 5 or rtu[1] != 3:
        raise IOError(f"unerwartete Antwort: {resp.hex()}")
    data = rtu[3:3 + rtu[2]]
    return list(struct.unpack(f">{len(data) // 2}H", data))


def main():
    blocks = [(0, 16), (59, 40), (100, 30), (500, 30), (586, 30), (672, 8)]
    with socket.create_connection((HOST, PORT), timeout=6) as sock:
        sock.settimeout(6)
        for seq, (start, count) in enumerate(blocks, 1):
            try:
                values = read_registers(sock, start, count, seq)
                print(f"{start:>4}: {values}")
            except Exception as exc:
                print(f"{start:>4}: Fehler: {exc}", file=sys.stderr)
            time.sleep(0.5)


if __name__ == "__main__":
    main()
