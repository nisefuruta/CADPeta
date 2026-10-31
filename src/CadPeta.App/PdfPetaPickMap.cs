using System.Collections.Generic;
using CadPeta.Ir;
using CadPeta.Transforms;

namespace CadPeta.App;

/// <summary>
/// スポイトの色番号の画像（<see cref="PdfPetaPipeline.RenderPickMap"/>）。画素ごとに、その画素の一番上に描いた図形の
/// 使用色の番号を持つ。画像の大きさは、同じ要求・同じ解像度で描いたプレビューの画像と同じ。
/// </summary>
public sealed class PdfPetaPickMap
{
    readonly int[] _indices;

    public PdfPetaPickMap(IReadOnlyList<ColorUse> colors, int[] indices, int width, int height)
    {
        Colors = colors; _indices = indices; Width = width; Height = height;
    }

    /// <summary>使用色の一覧（使用の多い順）。</summary>
    public IReadOnlyList<ColorUse> Colors { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>画素 (x, y) の図形の色。図形が無ければ <paramref name="radiusPx"/> 画素以内で最も近い図形の色、それも無ければ null。</summary>
    public IrRgb? ColorAt(int x, int y, int radiusPx)
    {
        int index = PickColorMap.FindNearest(_indices, Width, Height, x, y, radiusPx);
        // 番号の範囲の外は、一覧に無い色（PngWriter が色の無い文字に使う黒など）の画素
        return index >= 0 && index < Colors.Count ? Colors[index].Color : null;
    }
}
