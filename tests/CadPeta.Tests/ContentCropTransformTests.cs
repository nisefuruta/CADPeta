using CadPeta.Ir;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class ContentCropTransformTests
{
    [Fact]
    public void 背景除去後の内容境界に余白を付けてクロップする()
    {
        var ir = new IrDocument { WidthMm = 210, HeightMm = 297 };
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(10, 20), new IrLine(30, 40) },
            Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 2,
        });
        ConvertPipeline.Apply(ir, new ConvertOptions
        {
            RemoveBackground = false, ContentCrop = true, CropMarginMm = 4,
        }, new IrDiagnostics());
        Assert.Equal(5, ir.ViewMinXMm, 9);
        Assert.Equal(15, ir.ViewMinYMm, 9);
        Assert.Equal(30, ir.WidthMm, 9);
        Assert.Equal(30, ir.HeightMm, 9);
    }

    [Fact]
    public void 内容クロップと明示矩形は排他にする()
    {
        Assert.Throws<ArgumentException>(() => ConvertPipeline.Apply(new IrDocument(),
            new ConvertOptions { ContentCrop = true, CropRectMm = new CadPeta.Geometry.RectMm(0, 0, 1, 1) },
            new IrDiagnostics()));
    }

    [Fact]
    public void 深い入れ子グループを含んでも合併bboxとクロップ結果は不変()
    {
        // bbox 二重計算の解消（CropTransform への事前計算済み bbox 受け渡し）が
        // グループ再帰の結果を壊していないことを確認する。
        // group1(+5,+5) > group2(+3,+3) > group3(+2,+2) > path(0,0)-(10,10)
        // をグループローカルから合成すると、トップレベル座標では (10,10)-(20,20)。
        var innerPath = new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(10, 10) },
            Fill = new IrRgb(0, 0, 0),
        };
        var group3 = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 2, 2) };
        group3.Children.Add(innerPath);
        var group2 = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 3, 3) };
        group2.Children.Add(group3);
        var group1 = new IrGroup { Transform = new IrMatrix(1, 0, 0, 1, 5, 5) };
        group1.Children.Add(group2);

        // 別のトップレベルパス。ストローク幅2 → bbox は半幅1だけ外側へ広がる。
        var otherPath = new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(50, 60), new IrLine(55, 65) },
            Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 2,
        };

        var ir = new IrDocument { WidthMm = 210, HeightMm = 297 };
        ir.Nodes.Add(group1);
        ir.Nodes.Add(otherPath);

        ConvertPipeline.Apply(ir, new ConvertOptions
        {
            RemoveBackground = false, ContentCrop = true, CropMarginMm = 4,
        }, new IrDiagnostics());

        // 合併bbox = (10,10)-(56,66) に余白4mmを付けた範囲
        Assert.Equal(6, ir.ViewMinXMm, 9);
        Assert.Equal(6, ir.ViewMinYMm, 9);
        Assert.Equal(54, ir.WidthMm, 9);
        Assert.Equal(64, ir.HeightMm, 9);
        // 両ノードとも範囲内 → 間引かれず、入れ子構造もそのまま
        Assert.Equal(2, ir.Nodes.Count);
        Assert.Same(group1, ir.Nodes[0]);
        Assert.Same(otherPath, ir.Nodes[1]);
    }
}
