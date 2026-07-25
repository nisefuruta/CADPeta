// レイヤ別グループ化の単体テスト。
// レイヤ初出順・グループ内相対順の保存・移動要素数の定義・診断ログを検査する。
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Tests;

public class LayerGroupTransformTests
{
    static IrPath P(string? layer) => new()
    {
        Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(10, 10) },
        Stroke = new IrRgb(0, 0, 0),
        StrokeWidthMm = 0.25,
        DataLayer = layer,
    };

    static IrDocument Doc(params IrNode[] nodes)
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.AddRange(nodes);
        return ir;
    }

    [Fact]
    public void レイヤ初出順に再配列しグループ境界を設定する()
    {
        var a1 = P("A"); var b1 = P("B"); var a2 = P("A"); var c1 = P("C");
        var ir = Doc(a1, b1, a2, c1);

        int moved = LayerGroupTransform.Apply(ir, new IrDiagnostics());

        Assert.Equal(new IrNode[] { a1, a2, b1, c1 }, ir.Nodes);
        Assert.Equal(new[] { new IrLayerGroup("A", 2), new IrLayerGroup("B", 1), new IrLayerGroup("C", 1) },
                     ir.LayerGroups);
        // A の初出 run は a1 のみ（b1 で途切れる）→ 移動は a2 の1要素
        Assert.Equal(1, moved);
    }

    [Fact]
    public void グループ内は元の相対順を保存する()
    {
        var a1 = P("A"); var b1 = P("B"); var a2 = P("A"); var b2 = P("B"); var a3 = P("A");
        var ir = Doc(a1, b1, a2, b2, a3);

        LayerGroupTransform.Apply(ir, new IrDiagnostics());

        Assert.Equal(new IrNode[] { a1, a2, a3, b1, b2 }, ir.Nodes);
    }

    [Fact]
    public void 無レイヤ要素は専用グループ標示になる()
    {
        var n1 = P(null); var a1 = P("A");
        var ir = Doc(n1, a1);

        LayerGroupTransform.Apply(ir, new IrDiagnostics());

        Assert.Equal(new[]
        {
            new IrLayerGroup(LayerGroupTransform.UnnamedLayerLabel, 1),
            new IrLayerGroup("A", 1),
        }, ir.LayerGroups);
    }

    [Fact]
    public void 交錯がなければ移動要素数はゼロ()
    {
        var ir = Doc(P("A"), P("A"), P("B"), P("B"));

        int moved = LayerGroupTransform.Apply(ir, new IrDiagnostics());

        Assert.Equal(0, moved);
        Assert.Equal(2, ir.LayerGroups!.Count);
    }

    [Fact]
    public void 診断ログにレイヤ数と移動要素数を記録する()
    {
        var ir = Doc(P("A"), P("B"), P("A"));
        var diag = new IrDiagnostics();

        LayerGroupTransform.Apply(ir, diag);

        Assert.Contains(diag.Entries, e =>
            e.Type == "レイヤ別グループ化（z順の再配列＝非可逆）"
            && e.Target == "レイヤ数=2 移動要素数=1/3");
        Assert.Contains(diag.Entries, e =>
            e.Type == "レイヤ別グループ化（レイヤ初出順の再グループ）" && e.Target == "layer 'A' 2要素");
    }
}
