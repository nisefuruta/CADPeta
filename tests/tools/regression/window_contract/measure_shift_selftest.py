"""measure_shiftの前景近傍判定が移動を許容し、欠落を拒否することを確かめる。"""

from PIL import Image, ImageDraw

from measure_shift import foreground, translate, unmatched_percent


def make_fixture() -> Image.Image:
    image = Image.new("L", (100, 100), 255)
    draw = ImageDraw.Draw(image)
    draw.rectangle((10, 10, 90, 90), outline=0, width=2)
    draw.line((20, 50, 80, 50), fill=0, width=2)
    return image


base = foreground(make_fixture())
shifted = foreground(translate(make_fixture(), 2, -1, 255))
if unmatched_percent(base, shifted, 3) != 0 or unmatched_percent(shifted, base, 3) != 0:
    raise SystemExit("3px以内の平行移動を同一内容と判定できない")

missing_image = make_fixture()
ImageDraw.Draw(missing_image).rectangle((8, 45, 92, 55), fill=255)
missing = foreground(missing_image)
missed = unmatched_percent(base, missing, 3)
if missed <= 0.01:
    raise SystemExit(f"線分欠落を拒否できない: unmatched={missed}%")

print(f"measure_shift自己試験 OK: shift=0% missing={missed:.4f}%")
