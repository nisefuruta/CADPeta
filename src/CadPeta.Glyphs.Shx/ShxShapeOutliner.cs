// 複雑線種の記号（シェイプ）の輪郭化。文字ラン用の ShxGlyphOutliner とは異なり、
// 「above（キャップハイト基準）で正規化した文字高さ」という概念を持たない。
// 線種のシェイプ記号（例: ltypeshp.shx）は shape0（above/below/modes）を持たないファイルが
// 実在し、線種側の尺度指定（S=）がシェイプ設計単位への直接倍率として働く（AutoCAD の
// 複雑線種のカスタマイズ仕様どおり）。そのため本クラスはシェイプ設計単位のまま返し、
// 高さ正規化・Y反転・配置変換はすべて呼び出し側（線種の尺度・線種記号の配置規則を知る側）に委ねる。
using CadPeta.Ir;

namespace CadPeta.Glyphs.Shx;

public sealed class ShxShapeOutliner
{
    /// <summary>
    /// シェイプ番号1個を輪郭化する（シェイプ設計単位のまま、Y上向き、原点=シェイプの基準点）。
    /// シェイプ番号が索引に無ければ false。ファイルIO・SHXバイナリの解析エラーは握りつぶさず
    /// 例外として上へ透過させる（呼び出し側が catch して ex.ToString() を診断ログへ書く）。
    /// </summary>
    public bool TryOutlineShape(string shxPath, int shapeNumber,
                                out List<IrSegment>? segments, out string? failReason)
    {
        segments = null;
        failReason = null;

        var font = ShxFontFile.LoadCached(shxPath);
        if (!font.Glyphs.TryGetValue(shapeNumber, out var bytecode))
        {
            failReason = $"シェイプ番号 {shapeNumber} がフォントの索引に無い: {shxPath}";
            return false;
        }

        var walker = new ShxGlyphWalker(font.Kind, font.IsExtended,
            code => font.Glyphs.TryGetValue(code, out var b) ? b : null);
        // 線種のシェイプ記号（TRACK1/ZIG/BOX 等）は呼出元がペンダウン済みである前提で
        // 自身はペンダウンを発行しない（文字グリフとの違い。ShxGlyphWalker.Walk のコメント参照）
        var (localSegs, _, _) = walker.Walk(bytecode, initialPenDown: true);
        segments = localSegs;
        return true;
    }
}
