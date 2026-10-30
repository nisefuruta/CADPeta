// グリフ輪郭ポート（IGlyphOutliner）の SHX 実装の統合テスト。
// 本機に AutoCAD 2026 の Fonts フォルダが無い環境では Skip する。
using System;
using System.IO;
using System.Linq;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class ShxGlyphOutlinerTests
{
    const string FontsDir = @"C:\Program Files\Autodesk\AutoCAD 2026\Fonts";
    static string Romans => Path.Combine(FontsDir, "romans.shx");
    static string Extfont => Path.Combine(FontsDir, "extfont.shx");

    sealed class RequiresFontsAttribute : FactAttribute
    {
        public RequiresFontsAttribute()
        {
            if (!Directory.Exists(FontsDir))
                Skip = $"本機に AutoCAD 2026 の Fonts フォルダが無い: {FontsDir}";
        }
    }

    [RequiresFonts]
    public void capHeightMm基準でスケールしYを反転する()
    {
        // romans.shx 'A' の頂点はフォント単位(9,21)。above=21、FontSizeMm=3.0なので
        // scale=3.0/21。テキスト空間はY下向きのため頂点は(9*scale, -3.0)になる。
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.Shx("A", Romans, null, capHeightMm: 3.0, widthFactor: 1.0, glyphXsMm: null);
        Assert.True(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(fail);
        Assert.NotNull(segs);
        double scale = 3.0 / 21.0;
        var move = Assert.IsType<IrMove>(segs![0]);
        Assert.Equal(9 * scale, move.X, 9);
        Assert.Equal(-3.0, move.Y, 9); // -21*scale = -3.0
    }

    [RequiresFonts]
    public void 複数文字は字送りで積み上がる()
    {
        // "AA" を自然な字送り(GlyphXsMm無指定)で並べる。'A'の字送り幅(終端カーソルX)は18。
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.Shx("AA", Romans, null, capHeightMm: 21.0, widthFactor: 1.0, glyphXsMm: null);
        Assert.True(outliner.TryOutline(run, out var segs, out _));
        Assert.NotNull(segs);

        // FontSizeMm=21=above なので scale=1.0、2文字目の原点Xは18になるはず。
        var moves = segs!.OfType<IrMove>().ToList();
        Assert.Equal(6, moves.Count); // 'A'の頂点は3区間なのでIrMoveも3個×2文字
        // 2文字目の頂点(9,21)はX=18+9=27になる
        Assert.Contains(moves, m => Math.Abs(m.X - 27) < 1e-9 && Math.Abs(m.Y - (-21)) < 1e-9);
    }

    [RequiresFonts]
    public void 索引に無い文字は失敗し理由に文字を明記する()
    {
        // romans.shx はラテン文字のみのフォントで漢字グリフを持たない
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.Shx("仝", Romans, null, capHeightMm: 3.0, widthFactor: 1.0, glyphXsMm: null);
        Assert.False(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(segs);
        Assert.NotNull(fail);
        Assert.Contains("仝", fail);
    }

    [RequiresFonts]
    public void 半角スペースは失敗にせず字送りだけ進める()
    {
        var outliner = new ShxGlyphOutliner();
        // 空白のみの1文字ラン: romans.shx には code=0x0020 のグリフが実在するため
        // それを使って字送りする（フォールバック定数は使われない）。
        var run = GlyphRun.Shx(" ", Romans, null, capHeightMm: 21.0, widthFactor: 1.0, glyphXsMm: null);
        Assert.True(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(fail);
        Assert.NotNull(segs);
    }

    [RequiresFonts]
    public void BigFontPathがあれば漢字をビッグフォントから解決する()
    {
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.Shx("仝", Romans, Extfont, capHeightMm: 1.5, widthFactor: 1.0, glyphXsMm: null);
        Assert.True(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(fail);
        Assert.NotNull(segs);
        Assert.NotEmpty(segs!);

        // extfont.shx の above=15、scale=1.5/15=0.1。既知の直接描画区間の始点(1,1)は
        // テキスト空間で(0.1,-0.1)になるはず。
        Assert.Contains(segs!, s => s is IrMove m && Math.Abs(m.X - 0.1) < 1e-9 && Math.Abs(m.Y - (-0.1)) < 1e-9);
    }

    [Fact]
    public void SHXランでない場合は失敗する()
    {
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.TrueType("A", "Arial", 3.0, 1.0, null);
        Assert.False(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(segs);
        Assert.NotNull(fail);
    }

    [Fact]
    public void Outlineは失敗時にnullを返す()
    {
        var outliner = new ShxGlyphOutliner();
        var run = GlyphRun.TrueType("A", "Arial", 3.0, 1.0, null);
        Assert.Null(outliner.Outline(run));
    }
}
