// スポイトの色番号の画像: 図形の色を、使用色の一覧（ColorInventory）の番号を表す色へ塗り替える。
// この IR をアンチエイリアスなしで描くと、画素の色から、その画素の一番上に描いた図形の元の色が分かる
// （プレビューの画素は線の縁で背景と混ざり、近い色が多い図面では最も近い色が別の色になる）。
// 番号 i は 24 ビットの値 i + 1 の色にする。0（黒）と 0xFFFFFF（白、背景）は番号にしない。
using System;
using System.Collections.Generic;
using CadPeta.Ir;

namespace CadPeta.Transforms;

public static class PickColorMap
{
    /// <summary>図形が無い画素、または使用色の一覧に無い色の画素。</summary>
    public const int None = -1;

    const int MaxValue = 0xFFFFFF;

    /// <summary>
    /// 図形の色を番号の色へ塗り替え、不透明にして、画像を描かない（画像は色置換の対象外で、画素の色が番号と重なるため）。
    /// 画像は空のグループに置き換え、ノードの数を変えない（レイヤ別のグループ化の境界はノードの数で決まる）。
    /// 一覧に無い色はそのまま残す。透明（不透明度 0）の色は一覧に無いので、そのまま見えない。
    /// </summary>
    public static void Apply(IrDocument ir, IReadOnlyList<ColorUse> inventory)
    {
        if (ir is null) throw new ArgumentNullException(nameof(ir));
        if (inventory.Count >= MaxValue - 1)
            throw new ArgumentException($"使用色が多すぎて色番号の画像を作れない: {inventory.Count}色");
        var indexOf = new Dictionary<(int, int, int), int>();
        for (int i = 0; i < inventory.Count; i++) indexOf[inventory[i].Color.ToRgb8()] = i;

        IrRgb? Map(IrRgb? color, ref double opacity)
        {
            if (color is not { } c || opacity <= 0 || !indexOf.TryGetValue(c.ToRgb8(), out int index)) return color;
            opacity = 1;
            int value = index + 1;
            return new IrRgb((value >> 16) / 255d, ((value >> 8) & 0xFF) / 255d, (value & 0xFF) / 255d);
        }

        void Visit(List<IrNode> nodes)
        {
            for (int i = 0; i < nodes.Count; i++)
                if (nodes[i] is IrImage) nodes[i] = new IrGroup();
            foreach (var node in nodes)
                switch (node)
                {
                    case IrPath path:
                        path.Stroke = Map(path.Stroke, ref path.StrokeOpacity);
                        path.Fill = Map(path.Fill, ref path.FillOpacity);
                        break;
                    case IrText text:
                        text.Fill = Map(text.Fill, ref text.FillOpacity);
                        break;
                    case IrGroup group:
                        Visit(group.Children);
                        break;
                }
        }

        Visit(ir.Nodes);
    }

    /// <summary>画素の色から使用色の番号を求める。図形の無い画素（白）と番号でない色は <see cref="None"/>。</summary>
    public static int IndexOf(byte r, byte g, byte b)
    {
        int value = (r << 16) | (g << 8) | b;
        return value is 0 or MaxValue ? None : value - 1;
    }

    /// <summary>
    /// 画素の番号の並び（横 <paramref name="width"/>、行ごと）で、(x, y) の番号を返す。その画素に図形が無ければ、
    /// <paramref name="radius"/> 画素以内で最も近い図形の番号を返す（細い線をクリックしやすくする）。無ければ <see cref="None"/>。
    /// </summary>
    public static int FindNearest(int[] indices, int width, int height, int x, int y, int radius)
    {
        int best = None, bestDistance = int.MaxValue;
        for (int dy = -radius; dy <= radius; dy++)
            for (int dx = -radius; dx <= radius; dx++)
            {
                int px = x + dx, py = y + dy, distance = dx * dx + dy * dy;
                if (px < 0 || py < 0 || px >= width || py >= height || distance > radius * radius || distance >= bestDistance) continue;
                int index = indices[py * width + px];
                if (index == None) continue;
                best = index;
                bestDistance = distance;
            }
        return best;
    }
}
