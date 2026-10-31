# -*- coding: utf-8 -*-
# PDF内の「大面積の塗りパス」を列挙する（座標はmm換算で表示）
import re, sys, zlib

from pdf_matrix import mat_mul

path = sys.argv[1]
data = open(path, "rb").read()

streams = []
for m in re.finditer(rb"stream\r?\n", data):
    start = m.end()
    end = data.find(b"endstream", start)
    if end < 0:
        continue
    raw = data[start:end].rstrip(b"\r\n")
    try:
        dec = zlib.decompress(raw)
    except Exception:
        continue
    if re.search(rb"\d\s+(m|l|re)\b", dec):
        streams.append(dec)

print(f"content streams: {len(streams)}, bytes: {sum(len(s) for s in streams)}")

tok = re.compile(rb"(-?\d*\.?\d+)|([A-Za-z'*]+)")


PT2MM = 25.4 / 72.0
paths = []
ctm = [1, 0, 0, 1, 0, 0]
stack = []
cur_pts, nseg = [], 0
nums = []
fillcol = ("?",)

def xy(x, y):
    return (ctm[0]*x + ctm[2]*y + ctm[4], ctm[1]*x + ctm[3]*y + ctm[5])

def flush(kind):
    global cur_pts, nseg
    if cur_pts:
        xs = [p[0] for p in cur_pts]; ys = [p[1] for p in cur_pts]
        paths.append((kind, min(xs), max(xs), min(ys), max(ys), nseg, fillcol))
    cur_pts, nseg = [], 0

for s in streams:
    for m in tok.finditer(s):
        if m.group(1) is not None:
            nums.append(float(m.group(1)))
            continue
        op = m.group(2).decode()
        if op == "q":
            stack.append(ctm[:])
        elif op == "Q":
            if stack: ctm = stack.pop()
        elif op == "cm" and len(nums) >= 6:
            ctm = mat_mul(nums[-6:], ctm)
        elif op == "m" and len(nums) >= 2:
            cur_pts.append(xy(*nums[-2:]))
        elif op == "l" and len(nums) >= 2:
            cur_pts.append(xy(*nums[-2:])); nseg += 1
        elif op == "c" and len(nums) >= 6:
            n = nums[-6:]
            cur_pts += [xy(n[0], n[1]), xy(n[2], n[3]), xy(n[4], n[5])]; nseg += 1
        elif op in ("v", "y") and len(nums) >= 4:
            n = nums[-4:]
            cur_pts += [xy(n[0], n[1]), xy(n[2], n[3])]; nseg += 1
        elif op == "re" and len(nums) >= 4:
            x, y, w, h = nums[-4:]
            cur_pts += [xy(x, y), xy(x+w, y+h)]; nseg += 4
        elif op == "rg" and len(nums) >= 3:
            fillcol = tuple(round(v, 3) for v in nums[-3:])
        elif op == "g" and len(nums) >= 1:
            fillcol = (round(nums[-1], 3),) * 3
        elif op == "k" and len(nums) >= 4:
            fillcol = ("cmyk",) + tuple(round(v, 3) for v in nums[-4:])
        elif op in ("sc", "scn") and nums:
            fillcol = ("scn",) + tuple(round(v, 3) for v in nums[-3:])
        elif op in ("f", "F", "f*", "B", "B*", "b", "b*"):
            flush("fill")
        elif op in ("S", "s"):
            flush("stroke")
        elif op == "n":
            flush("clipnop")
        if op.isalpha() or "*" in op or "'" in op:
            nums = []

fills = [p for p in paths if p[0] == "fill"]
strokes = [p for p in paths if p[0] == "stroke"]
print(f"paths: total={len(paths)} fill={len(fills)} stroke={len(strokes)}")

# 幅5mm以上の塗りを面積降順で列挙
big = []
for k, x0, x1, y0, y1, nseg, col in fills:
    w = (x1-x0)*PT2MM; h = (y1-y0)*PT2MM
    if w > 5 and h > 1:
        big.append((w*h, x0*PT2MM, y0*PT2MM, w, h, nseg, col))
big.sort(reverse=True)
print(f"big fills (w>5mm): {len(big)}")
for a, x, y, w, h, nseg, col in big[:40]:
    print(f"  at({x:7.1f},{y:6.1f})mm size {w:6.1f}x{h:5.1f}mm seg={nseg} color={col}")
