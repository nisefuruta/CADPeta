// 色変換の単体テスト。
// 変換規則（純白維持・ISO 32000-1 輝度式・置換先行・画像対象外）と診断ログの集約記録を
// 検査する。
using System;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public class ColorTransformTests
{
    static IrPath Path(IrRgb? stroke, IrRgb? fill) => new()
    {
        Segments = DiagonalSegments(),
        Stroke = stroke,
        StrokeWidthMm = stroke != null ? 0.25 : 0,
        Fill = fill,
    };

    static readonly IrRgb White = new(1, 1, 1);
    static readonly IrRgb Black = new(0, 0, 0);
    static readonly IrRgb Red = new(1, 0, 0);
    static readonly ColorReplacement[] NoReplace = Array.Empty<ColorReplacement>();

    [Fact]
    public void モノクロ化は純白を維持し他を黒化する()
    {
        var wipeout = Path(null, White);              // ワイプアウト＝白塗り（隠蔽の意味を保つ）
        var redLine = Path(Red, null);
        var text = new IrText { Value = "A", XMm = 5, YMm = 5, FontSizeMm = 3, Fill = new IrRgb(0, 0, 1) };
        var ir = Square100Page(wipeout, redLine, text);

        ColorTransform.Apply(ir, ColorMode.Monochrome, NoReplace, new IrDiagnostics());

        Assert.Equal(White, wipeout.Fill);
        Assert.Equal(Black, redLine.Stroke);
        Assert.Equal(Black, text.Fill);
    }

    [Fact]
    public void グレースケール化はPDF仕様の輝度式を使う()
    {
        // 純緑: Gray = 0.59（ISO 32000-1 §10.4.2）→ 8bit 150
        var green = Path(new IrRgb(0, 1, 0), null);
        var ir = Square100Page(green);

        ColorTransform.Apply(ir, ColorMode.Grayscale, NoReplace, new IrDiagnostics());

        Assert.Equal("rgb(150,150,150)", green.Stroke!.Value.ToSvg());
        Assert.Equal(green.Stroke.Value.R, green.Stroke.Value.G);
        Assert.Equal(green.Stroke.Value.G, green.Stroke.Value.B);
    }

    [Fact]
    public void 置換はモードより先に適用される()
    {
        // 赤→白へ置換してからモノクロ化 → 赤だった要素だけ白で残る
        var red = Path(Red, null);
        var blue = Path(new IrRgb(0, 0, 1), null);
        var ir = Square100Page(red, blue);

        ColorTransform.Apply(ir, ColorMode.Monochrome,
            new[] { new ColorReplacement(Red, White) }, new IrDiagnostics());

        Assert.Equal(White, red.Stroke);
        Assert.Equal(Black, blue.Stroke);
    }

    [Fact]
    public void 置換は最初の一致だけを適用し連鎖しない()
    {
        var a = Path(Red, null);
        var ir = Square100Page(a);
        var green = new IrRgb(0, 1, 0);
        var blue = new IrRgb(0, 0, 1);

        // 赤→緑、緑→青 を並べても赤は緑で止まる（置換結果へ後続規則を連鎖適用しない）
        ColorTransform.Apply(ir, ColorMode.None,
            new[] { new ColorReplacement(Red, green), new ColorReplacement(green, blue) },
            new IrDiagnostics());

        Assert.Equal(green, a.Stroke);
    }

    [Fact]
    public void 量子化後に不変な変換は記録しない()
    {
        var black = Path(Black, null);
        var ir = Square100Page(black);
        var diag = new IrDiagnostics();

        ColorTransform.Apply(ir, ColorMode.Monochrome, NoReplace, diag);

        Assert.Equal(Black, black.Stroke);
        Assert.Empty(diag.Entries);   // 黒→黒は変換なし＝非可逆処理が起きていない
    }

    [Fact]
    public void 画像は対象外として診断ログに記録する()
    {
        var image = new IrImage { XMm = 0, YMm = 0, WidthMm = 10, HeightMm = 10, PngBytes = new byte[] { 1 } };
        var ir = Square100Page(image);
        var diag = new IrDiagnostics();

        ColorTransform.Apply(ir, ColorMode.Monochrome, NoReplace, diag);

        var entry = Assert.Single(diag.Entries);
        Assert.StartsWith("色変換の対象外（画像", entry.Type);
        Assert.Equal("image seq=0", entry.Target);
    }

    [Fact]
    public void 診断ログは色ごとの集約で件数を記録する()
    {
        var ir = Square100Page(Path(Red, null), Path(Red, null), Path(new IrRgb(0, 0, 1), null));
        var diag = new IrDiagnostics();

        ColorTransform.Apply(ir, ColorMode.Monochrome, NoReplace, diag);

        // 赤×2 と青×1 は「変換前色→変換後色」で集約され、ノード単位に膨らまない
        Assert.Equal(2, diag.Entries.Count);
        Assert.Contains(diag.Entries,
            e => e.Type == "色変換（モノクロ化）" && e.Target == "path stroke rgb(255,0,0)→rgb(0,0,0) 2件");
        Assert.Contains(diag.Entries,
            e => e.Type == "色変換（モノクロ化）" && e.Target == "path stroke rgb(0,0,255)→rgb(0,0,0) 1件");
    }

    [Fact]
    public void 透明度は変更しない()
    {
        var p = Path(Red, new IrRgb(1, 0, 0));
        p.StrokeOpacity = 0.5;
        p.FillOpacity = 0.3;
        var ir = Square100Page(p);

        ColorTransform.Apply(ir, ColorMode.Monochrome, NoReplace, new IrDiagnostics());

        Assert.Equal(0.5, p.StrokeOpacity);
        Assert.Equal(0.3, p.FillOpacity);
    }
}
