# -*- coding: utf-8 -*-
"""EMF のクラシックレコードを走査し、指定矩形（外枠）の論理座標を数値確認する。

使い方: probe_emf_coords.py <emf> <x0mm> <y0mm> <x1mm> <y1mm> [許容0.01mm単位]
論理単位は EmfOut の規約（0.01mm）。

走査対象は2系統:
- 絶対座標レコード POLYLINE16(87)/POLYLINE(4)/POLYGON16(86)/POLYGON(3)
  （コミット 67e7d4b 以降の生成規約。パスブラケット外は始点込み絶対座標のみ）
- MOVETOEX(27)+POLYLINETO16(89)/POLYLINETO(6) の並び（67e7d4b 以前の "To"系 EMF 用に維持）
"""
import struct
import sys

path = sys.argv[1]
want = [round(float(v) * 100) for v in sys.argv[2:6]]
tol = int(sys.argv[6]) if len(sys.argv) > 6 else 2
b = open(path, "rb").read()


def check(poly, off, rtype):
    xs = [p[0] for p in poly]
    ys = [p[1] for p in poly]
    if len(poly) < 4:
        return 0
    x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)
    if (abs(x0 - want[0]) <= tol and abs(y0 - want[1]) <= tol
            and abs(x1 - want[2]) <= tol and abs(y1 - want[3]) <= tol):
        print(f"一致レコード type={rtype} @0x{off:x}: 頂点={poly}")
        print(f"  論理bbox=({x0},{y0})-({x1},{y1}) [0.01mm]"
              f" = {(x1 - x0) / 100:.2f} x {(y1 - y0) / 100:.2f} mm")
        return 1
    return 0


off = 0
cur = None
found = 0
while off + 8 <= len(b):
    rtype, size = struct.unpack_from("<ii", b, off)
    if size < 8 or off + size > len(b):
        break
    if rtype == 27:  # MOVETOEX
        cur = struct.unpack_from("<ii", b, off + 8)
    elif rtype in (87, 86):  # POLYLINE16 / POLYGON16（絶対座標、16bit）
        (cpts,) = struct.unpack_from("<i", b, off + 24)
        pts = [struct.unpack_from("<hh", b, off + 28 + i * 4) for i in range(cpts)]
        found += check(pts, off, rtype)
    elif rtype in (4, 3):  # POLYLINE / POLYGON（絶対座標、32bit）
        (cpts,) = struct.unpack_from("<i", b, off + 24)
        pts = [struct.unpack_from("<ii", b, off + 28 + i * 8) for i in range(cpts)]
        found += check(pts, off, rtype)
    elif rtype in (89, 6) and cur is not None:  # POLYLINETO16 / POLYLINETO（"To"系）
        if rtype == 89:  # 16bit 版: bounds(16) + cpts(4) + int16 pairs
            (cpts,) = struct.unpack_from("<i", b, off + 24)
            pts = [struct.unpack_from("<hh", b, off + 28 + i * 4) for i in range(cpts)]
        else:  # 32bit 版
            (cpts,) = struct.unpack_from("<i", b, off + 24)
            pts = [struct.unpack_from("<ii", b, off + 28 + i * 8) for i in range(cpts)]
        poly = [cur] + pts
        found += check(poly, off, rtype)
        cur = poly[-1]
    if rtype == 14:
        break
    off += size

print(f"該当 {found} 件（目標 bbox=({want[0]},{want[1]})-({want[2]},{want[3]}) ±{tol}）")
