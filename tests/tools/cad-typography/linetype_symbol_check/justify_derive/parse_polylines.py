import re

path = r"C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltjust\derive_chart.svg"
data = open(path, encoding="utf-8").read()

S = 0.333339
OX = 6.6569
OY = 280.3810


def to_drawing(x, y):
    return (x - OX) / S, (OY - y) / S


paths = re.findall(r'<path d="([^"]+)"', data)
pts_per_path = []
for d in paths:
    nums = re.findall(r"-?\d+\.?\d*", d)
    nums = [float(n) for n in nums]
    pts = list(zip(nums[0::2], nums[1::2]))
    pts_per_path.append(pts)

# ブロックG/H（ポリライン）は図面X=1000-1020, drawingY=0(水平)/100(水平)+垂直脚。
horiz = []  # (drawingY0, x0, x1)
vert = []   # (drawingX0, y0, y1)
for pts in pts_per_path:
    if len(pts) != 2:
        continue
    (x1, y1), (x2, y2) = pts
    dx1, dy1 = to_drawing(x1, y1)
    dx2, dy2 = to_drawing(x2, y2)
    if abs(dy1 - dy2) < 1e-3 and 995 < dx1 < 1025 and 995 < dx2 < 1025:
        horiz.append((round(dy1, 2), min(dx1, dx2), max(dx1, dx2)))
    elif abs(dx1 - dx2) < 1e-3 and 1015 < dx1 < 1025:
        vert.append((round(dx1, 2), min(dy1, dy2), max(dy1, dy2)))

print("== 水平脚（drawingY, x0, x1, len） ==")
for y, x0, x1 in sorted(horiz, key=lambda t: (-t[0], t[1])):
    print(f"  y={y:7.2f}  x {x0:8.4f} - {x1:8.4f}  len={x1-x0:.4f}")

print("== 垂直脚（drawingX, y0, y1, len） ==")
for x, y0, y1 in sorted(vert, key=lambda t: (t[0], -t[1])):
    print(f"  x={x:7.2f}  y {y0:8.4f} - {y1:8.4f}  len={y1-y0:.4f}")
