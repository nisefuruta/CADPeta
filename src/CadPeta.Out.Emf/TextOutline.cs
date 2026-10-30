// クリップ境界と交差する文字の輪郭化（文字と交差するクリップは
// 輪郭化を要するため、非可逆処理として処理ログに記録する）。
// 輪郭の取得はグリフ輪郭ポート（TrueType 実装 = GdiGlyphOutliner）、
// 配置は GlyphPlacement の共通変換で行う。
using System.Collections.Generic;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Out.Emf;

internal static class TextOutline
{
    static readonly IGlyphOutliner Outliner = new GdiGlyphOutliner();

    /// <summary>文字ランをグリフ輪郭のセグメント列（mm、変換適用済み）へ展開する。</summary>
    public static List<IrSegment>? Outline(IrText t)
    {
        var run = GlyphRun.TrueType(t.Value, t.FontFamily, t.FontSizeMm, t.WidthFactor, t.GlyphXsMm);
        var textSpace = Outliner.Outline(run);
        if (textSpace == null)
            return null;
        return GlyphPlacement.Place(textSpace, t.XMm, t.YMm, t.RotationDeg, t.WidthFactor);
    }
}
