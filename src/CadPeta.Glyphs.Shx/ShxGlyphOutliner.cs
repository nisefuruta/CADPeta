// グリフ輪郭ポートの SHX 実装。文字ランをテキスト空間（原点=ベースライン左端、
// X右・Y下向き、mm、幅係数は未適用）のセグメント列へ展開する。
using System;
using System.Collections.Generic;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Glyphs.Shx;

public sealed class ShxGlyphOutliner : IGlyphOutliner
{
    // 空白はグリフを持たないため、above 相当を基準にした暫定の字送りで扱う
    // （実グリフが索引にあればそちらを優先するため、この定数はフォールバックのみで使う）。
    const double HalfWidthSpaceAdvanceFactor = 0.5; // 半角スペース: 全角の半分
    const double FullWidthSpaceAdvanceFactor = 1.0; // 全角スペース: 字高そのもの

    /// <summary>IGlyphOutliner の実装。失敗理由が要らない呼び出し向けの薄い被せ。</summary>
    public List<IrSegment>? Outline(GlyphRun run) =>
        TryOutline(run, out var segments, out _) ? segments : null;

    /// <summary>
    /// 文字ランを輪郭化する。失敗（CP932非対応・グリフ索引に無い文字がある等）なら false を返し
    /// failReason へ対象文字を明記する。ファイルIO・SHXバイナリの解析エラーはここで握りつぶさず
    /// 例外として上へ透過させる（呼び出し側が catch して ex.ToString() を診断ログへ書く）。
    /// </summary>
    public bool TryOutline(GlyphRun run, out List<IrSegment>? segments, out string? failReason)
    {
        segments = null;
        failReason = null;

        if (!run.IsShx || run.ShxPath == null)
        {
            failReason = "SHX ランでない（ShxPath 未指定）";
            return false;
        }

        var mainFont = ShxFontFile.LoadCached(run.ShxPath);
        var bigFont = run.BigFontPath != null ? ShxFontFile.LoadCached(run.BigFontPath) : null;

        // above（キャップハイト基準）が無いフォントは文字の輪郭化に使えない（DrawGlyph のスケール計算が
        // ゼロ除算になる）。shapes 形式には shape0 を持たない実物（複雑線種の記号シェイプ集）があり、
        // それをテキストスタイルへ誤って指定した場合の誤読を防ぐため、ここで明示的に失敗させる。
        if (mainFont.Above <= 0)
        {
            failReason = $"SHX ファイルにフォント諸元(above)が無くテキスト輪郭化に使えない" +
                         $"（線種記号専用の shapes 形式の可能性）: {run.ShxPath}";
            return false;
        }
        if (bigFont != null && bigFont.Above <= 0)
        {
            failReason = $"ビッグフォントにフォント諸元(above)が無くテキスト輪郭化に使えない" +
                         $"（線種記号専用の shapes 形式の可能性）: {run.BigFontPath}";
            return false;
        }

        int n = run.Text.Length;
        if (n == 0)
        {
            segments = new List<IrSegment>();
            return true;
        }

        bool useGlyphXs = run.GlyphXsMm != null && run.GlyphXsMm.Length == n;
        var segs = new List<IrSegment>();
        double penX = 0.0; // 幅係数を割り戻した自然な字送りの積算（GlyphXsMm 未指定時に使う）

        for (int idx = 0; idx < n; idx++)
        {
            char ch = run.Text[idx];
            // GlyphXsMm.規約: 配置済み座標系(回転前)でのX位置。幅係数は配置段(GlyphPlacement)で
            // 適用されるため、輪郭段では割り戻して自然空間のXへ戻す(GdiGlyphOutlinerと同一規約)。
            double originX = useGlyphXs
                ? (run.GlyphXsMm![idx] - run.GlyphXsMm![0]) / run.WidthFactor
                : penX;

            if (ch == ' ' || ch == '　')
            {
                double advanceMm;
                if (TryResolveChar(mainFont, bigFont, ch, out var spaceFont, out var spaceBytes, out _)
                    && spaceFont != null && spaceBytes != null)
                {
                    advanceMm = DrawGlyph(spaceFont, spaceBytes, run, originX, segs);
                }
                else
                {
                    double factor = ch == '　' ? FullWidthSpaceAdvanceFactor : HalfWidthSpaceAdvanceFactor;
                    advanceMm = factor * run.FontSizeMm; // above基準のスケール(FontSizeMm/above)×above=FontSizeMm
                }
                penX = originX + advanceMm;
                continue;
            }

            if (!TryResolveChar(mainFont, bigFont, ch, out var font, out var bytecode, out var charFail))
            {
                failReason = $"{charFail}（{idx}文字目、ラン全体: \"{run.Text}\"）";
                segments = null;
                return false;
            }

            double adv = DrawGlyph(font!, bytecode!, run, originX, segs);
            penX = originX + adv;
        }

        segments = segs;
        return true;
    }

    /// <summary>
    /// 文字→グリフの解決。BigFontPath があれば CP932 で符号化し、ビッグフォント索引に
    /// そのコードがあればビッグフォントのグリフ、無ければ通常フォント（unifont は Unicode
    /// コードポイントで引く）。BigFontPath が無ければ通常フォントのみを引く。
    /// </summary>
    static bool TryResolveChar(ShxFontFile mainFont, ShxFontFile? bigFont, char ch,
                               out ShxFontFile? font, out byte[]? bytecode, out string? failReason)
    {
        font = null; bytecode = null; failReason = null;

        if (bigFont != null)
        {
            if (!Cp932.TryGetCode(ch, out ushort code))
            {
                failReason = $"文字 '{ch}'(U+{(int)ch:X4}) は CP932 へ符号化できない";
                return false;
            }
            if (bigFont.Glyphs.TryGetValue(code, out var bb))
            {
                font = bigFont; bytecode = bb;
                return true;
            }
        }

        if (mainFont.Glyphs.TryGetValue(ch, out var mb))
        {
            font = mainFont; bytecode = mb;
            return true;
        }

        failReason = $"文字 '{ch}'(U+{(int)ch:X4}) のグリフが索引に無い";
        return false;
    }

    /// <summary>グリフ1個をテキスト空間へ描画して segs へ追加し、字送り量（mm、幅係数未適用）を返す。
    /// スケールは、そのグリフを供したフォントの above を基準に capHeightMm/above 倍する。</summary>
    static double DrawGlyph(ShxFontFile font, byte[] bytecode, GlyphRun run, double originX,
                            List<IrSegment> segs)
    {
        double scale = run.FontSizeMm / font.Above;
        var walker = new ShxGlyphWalker(font.Kind, font.IsExtended,
            code => font.Glyphs.TryGetValue(code, out var b) ? b : null);
        var (localSegs, endX, endY) = walker.Walk(bytecode);

        foreach (var s in localSegs)
        {
            segs.Add(s switch
            {
                // Y反転: テキスト空間はY下向き、SHXのフォント単位はY上向き。Xはそのまま右方向。
                IrMove m => new IrMove(originX + m.X * scale, -(m.Y * scale)),
                IrLine l => new IrLine(originX + l.X * scale, -(l.Y * scale)),
                IrCubic c => new IrCubic(originX + c.X1 * scale, -(c.Y1 * scale),
                                         originX + c.X2 * scale, -(c.Y2 * scale),
                                         originX + c.X * scale, -(c.Y * scale)),
                _ => throw new NotSupportedException($"未知の IrSegment 型: {s.GetType()}"),
            });
        }
        return endX * scale;
    }

}
