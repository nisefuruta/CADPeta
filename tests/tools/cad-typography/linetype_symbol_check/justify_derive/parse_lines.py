import re

path = r"C:\Users\wk\AppData\Local\Temp\cadpeta_p3b_ltjust\derive_chart.svg"
data = open(path, encoding="utf-8").read()

# 各 <path d="M x1 y1 L x2 y2 ..."> を M/L の点列へ分解する（本チャートは直線片のみ）。
paths = re.findall(r'<path d="([^"]+)"', data)

pts_per_path = []
for d in paths:
    nums = re.findall(r"-?\d+\.?\d*", d)
    nums = [float(n) for n in nums]
    pts = list(zip(nums[0::2], nums[1::2]))
    pts_per_path.append(pts)

# 水平線分（|y1-y2| がほぼ0）だけを対象にし、y でグループ化する（1グループ=1行=1エンティティ）。
# ブロックA/B/C（直線, 図面X 0..142）は svg X が 6.66..54 の範囲に収まる。
# ブロックG/H（ポリライン, 図面X 1000..1020）や円・円弧と svg Y が偶然一致することがあるため、
# X レンジで別ブロックを除外する。
X_MAX = 60.0
groups = {}
for pts in pts_per_path:
    if len(pts) != 2:
        continue
    (x1, y1), (x2, y2) = pts
    if abs(y1 - y2) > 1e-6:
        continue
    if max(x1, x2) > X_MAX:
        continue
    y_key = round(y1, 3)
    groups.setdefault(y_key, []).append((min(x1, x2), max(x1, x2)))

for y in sorted(groups.keys(), reverse=True):
    pieces = sorted(groups[y])
    print(f"y={y:.4f}  n={len(pieces)}  " + " | ".join(f"{a:.4f}-{b:.4f}(len={b-a:.4f})" for a, b in pieces))
