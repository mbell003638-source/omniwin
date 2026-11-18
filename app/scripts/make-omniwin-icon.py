"""Draws OmniWin's plain icon (no third-party brand): a ring with a dot, on a dark rounded square. Pure Python, no libraries.
Usage: python make-omniwin-icon.py <out.png> [size] [dark|light]"""
import math, struct, sys, zlib


def png(path, size, pixels):
    raw = b"".join(b"\x00" + bytes(pixels[y * size * 4:(y + 1) * size * 4]) for y in range(size))
    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def draw(size, background=True, colour=(91, 200, 255)):
    px = bytearray(size * size * 4)
    c = (size - 1) / 2
    radius_out, radius_in, dot = size * 0.36, size * 0.25, size * 0.09
    corner = size * 0.18
    for y in range(size):
        for x in range(size):
            dx, dy = x - c, y - c
            d = math.hypot(dx, dy)
            r, g, b, a = 0, 0, 0, 0
            if background:
                # rounded square
                qx, qy = max(abs(dx) - (c - corner), 0), max(abs(dy) - (c - corner), 0)
                if math.hypot(qx, qy) <= corner:
                    r, g, b, a = 24, 28, 38, 255
            if radius_in <= d <= radius_out or d <= dot:
                r, g, b, a = colour[0], colour[1], colour[2], 255
            i = (y * size + x) * 4
            px[i:i + 4] = bytes((r, g, b, a))
    return px


if __name__ == "__main__":
    out = sys.argv[1]
    size = int(sys.argv[2]) if len(sys.argv) > 2 else 1024
    mode = sys.argv[3] if len(sys.argv) > 3 else "dark"
    colour = {"dark": (91, 200, 255), "warning": (255, 176, 32), "recording": (240, 72, 72)}.get(mode, (91, 200, 255))
    png(out, size, draw(size, background=(mode == "dark"), colour=colour))
