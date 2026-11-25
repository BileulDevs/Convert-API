"""Writes a Full HD gradient PNG to the given path: enough pixels to make a conversion real work."""
import struct
import sys
import zlib

WIDTH, HEIGHT = 1920, 1080


def chunk(kind: bytes, data: bytes) -> bytes:
    return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))


rows = b"".join(
    b"\x00" + bytes(channel for x in range(WIDTH) for channel in (x * 255 // WIDTH, y * 255 // HEIGHT, (x + y) % 256))
    for y in range(HEIGHT)
)
png = (
    b"\x89PNG\r\n\x1a\n"
    + chunk(b"IHDR", struct.pack(">IIBBBBB", WIDTH, HEIGHT, 8, 2, 0, 0, 0))
    + chunk(b"IDAT", zlib.compress(rows, 6))
    + chunk(b"IEND", b"")
)
with open(sys.argv[1], "wb") as file:
    file.write(png)
