// グループ平坦化（変換行列の焼き込み）の単体テスト。
// EMF 出力の前段が使う変換の正確さと、グループ非対応コードへの波及がないことを検査する。
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Tests;

public class GroupFlattenTests
{
    const int Precision = 9; // 変換は倍精度演算のみで、この桁まで数値一致する

    static IrPath Line(double x0, double y0, double x1, double y1, double width = 0.25) => new()
    {
        Segments = new List<IrSegment> { new IrMove(x0, y0), new IrLine(x1, y1) },
        Stroke = new IrRgb(0, 0, 0),
        StrokeWidthMm = width,
    };

    [Fact]
    public void グループが無ければ入力をそのまま返す()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.Add(Line(0, 0, 10, 0));
        var diag = new IrDiagnostics();
        Assert.Same(ir, GroupFlatten.Apply(ir, diag));
        Assert.Empty(diag.Entries);
    }

    [Fact]
    public void 変換行列を座標と線幅へ焼き込む()
    {
        // 回転90度（時計回り、Y下向き座標系）＋2倍＋平行移動(100,50):
        // matrix(0, 2, -2, 0, 100, 50) → (x,y) → (100-2y, 50+2x)
        var ir = new IrDocument { WidthMm = 200, HeightMm = 200 };
        var g = new IrGroup { Transform = new IrMatrix(0, 2, -2, 0, 100, 50) };
        g.Children.Add(Line(1, 0, 3, 0, width: 0.25));
        ir.Nodes.Add(g);

        var diag = new IrDiagnostics();
        var flat = GroupFlatten.Apply(ir, diag);

        var p = Assert.IsType<IrPath>(Assert.Single(flat.Nodes));
        var m0 = Assert.IsType<IrMove>(p.Segments[0]);
        var l1 = Assert.IsType<IrLine>(p.Segments[1]);
        Assert.Equal(100, m0.X, Precision);
        Assert.Equal(52, m0.Y, Precision);
        Assert.Equal(100, l1.X, Precision);
        Assert.Equal(56, l1.Y, Precision);
        Assert.Equal(0.5, p.StrokeWidthMm, Precision);
        // 相似変換は近似ログを出さない
        Assert.Empty(diag.Entries);
        // 入力は不変
        Assert.IsType<IrGroup>(Assert.Single(ir.Nodes));
    }

    [Fact]
    public void 入れ子グループは行列を合成する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var inner = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 5, 0) };
        inner.Children.Add(Line(0, 0, 1, 0));
        var outer = new IrGroup { Transform = new IrMatrix(2, 0, 0, 2, 10, 20) };
        outer.Children.Add(inner);
        ir.Nodes.Add(outer);

        var flat = GroupFlatten.Apply(ir, new IrDiagnostics());
        var p = Assert.IsType<IrPath>(Assert.Single(flat.Nodes));
        var m0 = Assert.IsType<IrMove>(p.Segments[0]);
        // 内側(0,0) → 内側変換(5,0) → 外側変換(2倍+10,20) = (20,20)
        Assert.Equal(20, m0.X, Precision);
        Assert.Equal(20, m0.Y, Precision);
    }

    [Fact]
    public void 文字は原点と回転と寸法を写す()
    {
        var ir = new IrDocument { WidthMm = 200, HeightMm = 200 };
        // 回転90度（時計回り）＋2倍
        var g = new IrGroup { Transform = new IrMatrix(0, 2, -2, 0, 100, 50) };
        g.Children.Add(new IrText
        {
            Value = "AB", XMm = 10, YMm = 0, FontSizeMm = 3,
            GlyphXsMm = new[] { 10.0, 12.5 },
        });
        ir.Nodes.Add(g);

        var flat = GroupFlatten.Apply(ir, new IrDiagnostics());
        var t = Assert.IsType<IrText>(Assert.Single(flat.Nodes));
        Assert.Equal(100, t.XMm, Precision);
        Assert.Equal(70, t.YMm, Precision);
        Assert.Equal(6, t.FontSizeMm, Precision);
        Assert.Equal(90, t.RotationDeg, Precision);
        // グリフ位置はベースライン射影距離（XMm 起点）: 間隔2.5mm → 2倍で5mm
        Assert.NotNull(t.GlyphXsMm);
        Assert.Equal(100, t.GlyphXsMm![0], Precision);
        Assert.Equal(105, t.GlyphXsMm[1], Precision);
    }

    [Fact]
    public void 非等方変換は線幅近似を診断ログへ記録する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var g = new IrGroup { Transform = new IrMatrix(3, 0, 0, 1, 0, 0) };
        g.Children.Add(Line(0, 0, 1, 0));
        ir.Nodes.Add(g);

        var diag = new IrDiagnostics();
        GroupFlatten.Apply(ir, diag);
        Assert.Contains(diag.Entries, e => e.Type.Contains("非等方変換の線幅近似"));
    }

    [Fact]
    public void 破線パターンもスケールする()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var g = new IrGroup { Transform = new IrMatrix(2, 0, 0, 2, 0, 0) };
        var dashed = Line(0, 0, 10, 0);
        dashed.StrokeDashMm = new[] { 1.2, 0.6 };
        g.Children.Add(dashed);
        ir.Nodes.Add(g);

        var flat = GroupFlatten.Apply(ir, new IrDiagnostics());
        var p = Assert.IsType<IrPath>(Assert.Single(flat.Nodes));
        Assert.Equal(new[] { 2.4, 1.2 }, p.StrokeDashMm!.Select(d => Math.Round(d, 9)));
    }

    [Fact]
    public void グループのクリップは子へ引き継ぐ()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var g = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 10, 0), ClipId = "c0" };
        g.Children.Add(Line(0, 0, 1, 0));
        ir.Nodes.Add(g);

        var flat = GroupFlatten.Apply(ir, new IrDiagnostics());
        Assert.Equal("c0", flat.Nodes[0].ClipId);
    }

    [Fact]
    public void レイヤ別グループ境界は展開後の要素数で数え直す()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var g = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 5, 5), DataLayer = "A" };
        g.Children.Add(Line(0, 0, 1, 0));
        g.Children.Add(Line(0, 1, 1, 1));
        var single = Line(50, 50, 60, 60);
        single.DataLayer = "B";
        ir.Nodes.Add(g);
        ir.Nodes.Add(single);
        ir.LayerGroups = new List<IrLayerGroup> { new("A", 1), new("B", 1) };

        var flat = GroupFlatten.Apply(ir, new IrDiagnostics());
        Assert.Equal(3, flat.Nodes.Count);
        Assert.Equal(new[] { ("A", 2), ("B", 1) },
                     flat.LayerGroups!.Select(x => (x.Label, x.Count)));
        // 直列化の整合検査（総和=ノード数）を通ること
        SvgWriter.Write(flat);
    }

    [Fact]
    public void グループのbboxは変換行列込みで保守的に取れる()
    {
        var g = new IrGroup { Transform = new IrMatrix(2, 0, 0, 2, 100, 0) };
        g.Children.Add(Line(0, 0, 5, 5, width: 1));
        var b = IrBounds.NodeBbox(g);
        // 子 bbox（線幅半分 0.5 拡張）= (-0.5,-0.5)-(5.5,5.5) → 2倍+100 = (99,-1)-(111,11)
        Assert.Equal(99, b[0], Precision);
        Assert.Equal(-1, b[1], Precision);
        Assert.Equal(111, b[2], Precision);
        Assert.Equal(11, b[3], Precision);
    }

    [Fact]
    public void 色変換はグループの子孫にも届く()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var inner = new IrGroup();
        var red = Line(0, 0, 1, 0);
        red.Stroke = new IrRgb(1, 0, 0);
        inner.Children.Add(red);
        var outer = new IrGroup();
        outer.Children.Add(inner);
        ir.Nodes.Add(outer);

        ColorTransform.Apply(ir, ColorMode.Monochrome,
                             Array.Empty<ColorReplacement>(), new IrDiagnostics());
        Assert.Equal(new IrRgb(0, 0, 0), red.Stroke);
    }
}
