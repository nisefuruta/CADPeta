import re
import math

path = r"C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltjust\derive_chart.svg"
data = open(path, encoding="utf-8").read()

# 較正済みアフィン変換（parse_lines.py の直線データから導出）。
S = 0.333339
OX = 6.6569
OY = 280.3810


def to_drawing(x, y):
    return (x - OX) / S, (OY - y) / S


def endpoints_of(d):
    """d 属性から M の始点と各コマンドの終点（制御点は無視）を返す。"""
    tokens = re.findall(r"[MLC]|-?\d+\.?\d*", d)
    pts = []
    i = 0
    cur_cmd = None
    while i < len(tokens):
        t = tokens[i]
        if t in ("M", "L", "C"):
            cur_cmd = t
            i += 1
            continue
        if cur_cmd == "M":
            x, y = float(tokens[i]), float(tokens[i + 1])
            pts.append((x, y))
            i += 2
        elif cur_cmd == "L":
            x, y = float(tokens[i]), float(tokens[i + 1])
            pts.append((x, y))
            i += 2
        elif cur_cmd == "C":
            # 3組の座標（制御点1・制御点2・終点）。終点だけ採用する。
            x, y = float(tokens[i + 4]), float(tokens[i + 5])
            pts.append((x, y))
            i += 6
        else:
            i += 1
    return pts


paths = re.findall(r'<path d="([^"]+)"', data)
all_pts = [endpoints_of(d) for d in paths]

# 円の中心（図面座標 700,y）と目標円周C・既知半径 R=C/2πごとに割り当てる。
targets_derive = list(zip((0, 60, 120, 180, 240, 300, 360), (40, 45, 50, 60, 70, 100, 110)))
targets_holdout = list(zip((440, 500, 560), (55, 85, 130)))
targets = targets_derive + targets_holdout

for cy, c_target in targets:
    cx = 700
    r_expected = c_target / (2 * math.pi)
    pieces = []
    for pts in all_pts:
        if len(pts) < 2:
            continue
        dpts = [to_drawing(x, y) for x, y in pts]
        dists = [math.hypot(px - cx, py - cy) for px, py in dpts]
        avg_r = sum(dists) / len(dists)
        if abs(avg_r - r_expected) > 0.5 or max(dists) - min(dists) > 0.3:
            continue
        a0 = math.atan2(dpts[0][1] - cy, dpts[0][0] - cx)
        a1 = math.atan2(dpts[-1][1] - cy, dpts[-1][0] - cx)
        pieces.append((a0, a1, avg_r))
    if not pieces:
        print(f"C={c_target} (cy={cy}) 該当パスなし")
        continue
    r_est = sum(p[2] for p in pieces) / len(pieces)
    print(f"C={c_target} (cy={cy}) r~{r_est:.5f} circumference~{2*math.pi*r_est:.5f} n_pieces={len(pieces)}")
    # 角度でソートして並び順を見やすくする
    def norm(a):
        return a if a >= 0 else a + 360
    ordered = sorted(pieces, key=lambda p: norm(math.degrees(p[0])))
    for a0, a1, r in ordered:
        span = math.degrees(a1) - math.degrees(a0)
        if span < -180:
            span += 360
        if span > 180:
            span -= 360
        arclen = abs(span) * math.pi / 180 * r
        print(f"    angle {math.degrees(a0):8.3f} -> {math.degrees(a1):8.3f}  span={span:8.3f}deg  arclen={arclen:.4f}")
