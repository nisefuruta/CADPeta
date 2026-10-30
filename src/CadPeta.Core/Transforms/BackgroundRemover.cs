// 変換層1段目: 背景矩形の除去（PDF印刷経路由来のIRのみ対象）。
// 三条件「描画順が最初期・ページ範囲とほぼ一致・塗りのみ」で同定し除去する。
// AutoCAD PDF系ドライバでは出現しない実測（6PDFで0件）だが、他ドライバ・他CAD由来の
// PDF への防御として維持する（実物図面での実測で確認）。
using System;
using CadPeta.Geometry;
using CadPeta.Ir;

namespace CadPeta.Transforms;

public static class BackgroundRemover
{
    /// <summary>診断カテゴリ（呼び出し側が「背景除去が起きたか」を機械判定する際の基準名）。</summary>
    public const string DiagCategory = "背景矩形の除去（描画順最初期・ページ範囲とほぼ一致・塗りのみ）";

    /// <summary>先頭から連続する背景矩形を除去し、除去件数を返す。診断ログに対象を記録する。</summary>
    public static int Apply(IrDocument ir, IrDiagnostics diag)
    {
        int removed = 0;
        while (ir.Nodes.Count > 0
               && ir.Nodes[0] is IrPath bg && bg.Fill != null && bg.Stroke == null
               && CoversPage(GeoClip.Bbox(bg.Segments), ir.WidthMm, ir.HeightMm))
        {
            diag.Add(DiagCategory,
                     $"node seq=0 fill={bg.Fill.Value.ToSvg()} {GeoClip.BboxText(GeoClip.Bbox(bg.Segments))}");
            ir.Nodes.RemoveAt(0);
            removed++;
        }
        return removed;
    }

    // 「ページ範囲とほぼ一致」の判定閾値（背景除去の三条件の一つ）。用紙縁の余白・丸めを吸収しつつ、
    // 図面本体の大きな矩形（図枠等）を背景と誤認しない距離として ±2mm を採る
    const double PageEdgeTolMm = 2.0;

    static bool CoversPage(double[] b, double wMm, double hMm) =>
        Math.Abs(b[0]) <= PageEdgeTolMm && Math.Abs(b[1]) <= PageEdgeTolMm
        && Math.Abs(b[2] - wMm) <= PageEdgeTolMm && Math.Abs(b[3] - hMm) <= PageEdgeTolMm;
}
