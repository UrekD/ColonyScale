#!/usr/bin/env python3
"""Convert an Xvfb -fbdir screen dump (XWD, 24/32bpp TrueColor) to PNG. Stdlib only.
Usage: xwd2png.py <Xvfb_screen0> <out.png>"""
import struct, sys, zlib

def main(src, dst):
    data = open(src, "rb").read()
    h = struct.unpack(">25I", data[:100])
    header_size, width, height = h[0], h[4], h[5]
    byte_order, bpp, bpl, ncolors = h[7], h[11], h[12], h[19]
    off = header_size + ncolors * 12
    if bpp != 32:
        sys.exit(f"unsupported bpp {bpp}")
    rows = []
    for y in range(height):
        line = data[off + y * bpl: off + y * bpl + width * 4]
        if byte_order == 0:  # LSBFirst: B G R X
            px = bytearray(width * 3)
            px[0::3], px[1::3], px[2::3] = line[2::4], line[1::4], line[0::4]
        else:
            px = bytearray(width * 3)
            px[0::3], px[1::3], px[2::3] = line[1::4], line[2::4], line[3::4]
        rows.append(b"\x00" + bytes(px))
    def chunk(t, d):
        c = struct.pack(">I", len(d)) + t + d
        return c + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)
    png = b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(b"".join(rows), 6)) + chunk(b"IEND", b"")
    open(dst, "wb").write(png)

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
