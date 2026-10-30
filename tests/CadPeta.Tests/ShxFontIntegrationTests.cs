// 本機実物のSHX/ビッグフォント（AutoCAD 2026 同梱）を使う統合テスト。
// 実測値の出典は tests/tools/cad-typography/shx_binary_survey.py の出力
// （本機実測、C:\Users\wk\AppData\Local\Temp\claude\...\scratchpad\survey_output.txt、2026-07-10）。
// 本機に AutoCAD 2026 の Fonts フォルダが無い環境では Skip する。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class ShxFontIntegrationTests
{
    const string FontsDir = @"C:\Program Files\Autodesk\AutoCAD 2026\Fonts";

    static string P(string name) => Path.Combine(FontsDir, name);

    /// <summary>フォント自身のグリフ表を引くバイトコード解釈器。</summary>
    static ShxGlyphWalker WalkerFor(ShxFontFile font) =>
        new(font.Kind, font.IsExtended, code => font.Glyphs.TryGetValue(code, out var b) ? b : null);

    /// <summary>本機に AutoCAD 2026 の Fonts フォルダが無ければ Skip する条件付き Fact。</summary>
    sealed class RequiresFontsAttribute : FactAttribute
    {
        public RequiresFontsAttribute()
        {
            if (!Directory.Exists(FontsDir))
                Skip = $"本機に AutoCAD 2026 の Fonts フォルダが無い: {FontsDir}";
        }
    }

    [RequiresFonts]
    public void 実物6ファイルの形式判定とグリフ数above_below拡張フラグが実測値と一致する()
    {
        // survey_output.txt のグリフ数は bigfont では shape0(code=0) を含むが、本実装は
        // shape0 をフォント諸元として Glyphs から除外する設計のため、bigfont系4ファイルのみ
        // 実測値-1 が期待値になる（unifont の2ファイルは元々 shape0 を Glyphs に含めない）。
        AssertFont(P("romans.shx"), ShxFontKind.Unifont, glyphCount: 299, above: 21, below: 7, isExtended: false);
        AssertFont(P("txt.shx"), ShxFontKind.Unifont, glyphCount: 286, above: 6, below: 2, isExtended: false);
        AssertFont(P("extfont.shx"), ShxFontKind.BigFont, glyphCount: 7392 - 1, above: 15, below: 0, isExtended: true);
        AssertFont(P("bigfont.shx"), ShxFontKind.BigFont, glyphCount: 3664 - 1, above: 15, below: 0, isExtended: false);
        AssertFont(P("extfont2.shx"), ShxFontKind.BigFont, glyphCount: 8316 - 1, above: 15, below: 0, isExtended: true);
        AssertFont(P("@extfont2.shx"), ShxFontKind.BigFont, glyphCount: 8316 - 1, above: 15, below: 0, isExtended: true);
    }

    static void AssertFont(string path, ShxFontKind kind, int glyphCount, int above, int below, bool isExtended)
    {
        var font = ShxFontFile.Load(path);
        Assert.Equal(kind, font.Kind);
        Assert.Equal(glyphCount, font.Glyphs.Count);
        Assert.Equal(above, font.Above);
        Assert.Equal(below, font.Below);
        Assert.Equal(isExtended, font.IsExtended);
    }

    [RequiresFonts]
    public void ltypeshpのshapes形式は6シェイプの番号と名前がsurvey実測と一致する()
    {
        // 実測(本記録の実バイトトレース、tests/tools/cad-typography/shapes_probe.py 相当の手計算検証):
        // header: first=0x82(130) last=0x87(135) count=6
        // 索引: (130,13,"TRACK1") (131,7,"ZIG") (132,10,"BOX") (133,10,"CIRC1")
        //       (134,10,"BAT") (135,23,"AMZIGZAG")。shape0(code=0)は無い。
        var font = ShxFontFile.Load(P("ltypeshp.shx"));
        Assert.Equal(ShxFontKind.Shapes, font.Kind);
        Assert.False(font.HasShape0);
        Assert.Equal(0, font.Above);
        Assert.False(font.IsExtended);

        Assert.Equal(6, font.Glyphs.Count);
        Assert.Equal(6, font.ShapeNames.Count);
        var expectedNames = new Dictionary<string, int>
        {
            ["TRACK1"] = 130, ["ZIG"] = 131, ["BOX"] = 132,
            ["CIRC1"] = 133, ["BAT"] = 134, ["AMZIGZAG"] = 135,
        };
        foreach (var kv in expectedNames)
        {
            Assert.Equal(kv.Value, font.ShapeNames[kv.Key]);
            Assert.True(font.Glyphs.ContainsKey(kv.Value), $"code={kv.Value}({kv.Key}) がGlyphsに無い");
        }
    }

    [RequiresFonts]
    public void ltypeshpの全シェイプ走査がエラーゼロで完走しCIRC1は全周弧で始点に戻る()
    {
        var font = ShxFontFile.Load(P("ltypeshp.shx"));
        var walker = WalkerFor(font);

        foreach (var kv in font.Glyphs)
        {
            var (segs, _, _) = walker.Walk(kv.Value); // 例外が飛べばテスト失敗
            Assert.NotNull(segs);
        }

        // CIRC1(133)はオクタント弧(半径1、count=0=8オクタント=全周)。全周なので
        // 開始角と終了角が一致し、ペンダウン状態なら始点へ戻る。
        var circ1Walker = WalkerFor(font);
        byte[] circ1WithPenDown = new byte[] { 1 }.Concat(font.Glyphs[133]).ToArray();
        var (circSegs, circEndX, circEndY) = circ1Walker.Walk(circ1WithPenDown);
        Assert.Equal(0, circEndX, 6);
        Assert.Equal(0, circEndY, 6);
        Assert.Contains(circSegs, s => s is IrCubic);
    }

    [RequiresFonts]
    public void 全グリフ走査がエラーゼロで完走する()
    {
        string[] files =
        {
            "romans.shx", "txt.shx", "extfont.shx", "bigfont.shx", "extfont2.shx", "@extfont2.shx",
            "ltypeshp.shx",
        };
        int total = 0;
        var errors = new List<string>();
        foreach (var name in files)
        {
            var font = ShxFontFile.Load(P(name));
            var walker = WalkerFor(font);
            foreach (var kv in font.Glyphs)
            {
                total++;
                try { walker.Walk(kv.Value); }
                catch (Exception ex) { errors.Add($"{name} code=0x{kv.Key:x4}: {ex.GetType().Name}: {ex.Message}"); }
            }
        }
        Assert.True(errors.Count == 0,
            $"{errors.Count}/{total} グリフでエラー: {string.Join("; ", errors.Take(10))}");
        Assert.True(total > 0, "走査対象グリフが0件（フォント読込自体が失敗している疑い）");
    }

    [RequiresFonts]
    public void romansのAのストロークがsurvey実測と一致する()
    {
        // survey_output.txt: 区間0:(9,21)-(1,0) 区間1:(9,21)-(17,0) 区間2:(4,7)-(14,7)
        // 終端カーソル位置:(18,0)。'A' はベジェ化しない直線のみで構成される。
        var font = ShxFontFile.Load(P("romans.shx"));
        var walker = WalkerFor(font);
        var (segs, endX, endY) = walker.Walk(font.Glyphs[0x41]); // 'A'

        Assert.Equal(18, endX, 9);
        Assert.Equal(0, endY, 9);

        var expected = new IrSegment[]
        {
            new IrMove(9, 21), new IrLine(1, 0),
            new IrMove(9, 21), new IrLine(17, 0),
            new IrMove(4, 7), new IrLine(14, 7),
        };
        AssertSegsEqual(expected, segs);
    }

    [RequiresFonts]
    public void extfontの仝のデコードがsurvey実測の直接描画部分を含む()
    {
        // survey_output.txt: 採用コード 0x8157 → CP932 '仝'（タスク仕様書が挙げた「0x8253付近」は
        // 実測と異なる。正しいコードは survey_output.txt 実測の 0x8157）。
        //
        // 想定外の発見: survey スクリプトの Walker は opcode7（サブシェイプ呼出）を
        // 「バイト境界のスキップのみ」で処理し、参照先グリフの幾何を実際には合成しない
        // （調査目的のバイト境界検証に特化した実装のため）。'仝' のバイトコードは拡張形式
        // (07,00,...) と通常形式 (07,<n>) のサブシェイプ呼出を含み、survey の
        // 「区間0-2」はそのうちサブシェイプ呼出を経由しない直接描画命令だけの結果である。
        // 本実装（製品コード）はサブシェイプを実際に合成するため、survey の3区間より
        // 多いセグメントを産む（合成した分だけ豊かなのが正しい挙動）。
        // 終端カーソル位置(18,0)はsurvey実測と一致し、直接描画区間の3本の線分は
        // 合成後の出力にすべて含まれることをここで検証する（バイト境界の整合性の裏付け）。
        var font = ShxFontFile.Load(P("extfont.shx"));
        var walker = WalkerFor(font);
        var (segs, endX, endY) = walker.Walk(font.Glyphs[0x8157]);

        Assert.Equal(18, endX, 9);
        Assert.Equal(0, endY, 9);
        Assert.True(segs.Count >= 6, $"survey実測の3区間(6セグメント)以上を期待: 実際={segs.Count}");

        AssertContainsMoveTo(segs, 1, 1);
        AssertContainsLineTo(segs, 13, 1);
        AssertContainsMoveTo(segs, 7, 8);
        AssertContainsLineTo(segs, 7, 1);
        AssertContainsMoveTo(segs, 3, 8);
        AssertContainsLineTo(segs, 11, 8);
    }

    static void AssertContainsMoveTo(IEnumerable<IrSegment> segs, double x, double y) =>
        Assert.Contains(segs, s => s is IrMove m && Approx(m.X, x) && Approx(m.Y, y));

    static void AssertContainsLineTo(IEnumerable<IrSegment> segs, double x, double y) =>
        Assert.Contains(segs, s => s is IrLine l && Approx(l.X, x) && Approx(l.Y, y));

    static bool Approx(double a, double b) => Math.Abs(a - b) < 1e-9;

    static void AssertSegsEqual(IReadOnlyList<IrSegment> expected, IReadOnlyList<IrSegment> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            switch (expected[i])
            {
                case IrMove em:
                    var am = Assert.IsType<IrMove>(actual[i]);
                    Assert.Equal(em.X, am.X, 9); Assert.Equal(em.Y, am.Y, 9);
                    break;
                case IrLine el:
                    var al = Assert.IsType<IrLine>(actual[i]);
                    Assert.Equal(el.X, al.X, 9); Assert.Equal(el.Y, al.Y, 9);
                    break;
                default:
                    throw new NotSupportedException($"未対応の期待セグメント型: {expected[i].GetType()}");
            }
        }
    }
}
