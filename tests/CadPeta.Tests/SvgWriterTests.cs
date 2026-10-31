// SvgWriter（IR の正準直列化）の単体テスト。生成規約の要点を検査する。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public class SvgWriterTests
{
    [Fact]
    public void 物理寸法とviewBoxを書き出す()
    {
        var ir = new IrDocument { WidthMm = 150, HeightMm = 90, ViewMinXMm = 90, ViewMinYMm = 125 };
        var svg = SvgWriter.Write(ir);
        Assert.Contains("width=\"150mm\" height=\"90mm\"", svg);
        Assert.Contains("viewBox=\"90 125 150 90\"", svg);
    }

    [Fact]
    public void 連続空白を含む文字には空白保持を直付けする()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.Add(new IrText { Value = "縮尺  1:150", XMm = 10, YMm = 10, FontSizeMm = 3 });
        var svg = SvgWriter.Write(ir);
        Assert.Contains("xml:space=\"preserve\"", svg);
        Assert.Contains("white-space:pre", svg);
    }

    [Fact]
    public void transformを持つ文字のクリップは親gへ退避する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.ClipDefs.Add(Square50Clip("clip0"));
        ir.Nodes.Add(new IrText
        {
            Value = "12000", XMm = 10, YMm = 10, FontSizeMm = 3,
            RotationDeg = -90, ClipId = "clip0",
        });
        var svg = SvgWriter.Write(ir);
        Assert.Contains("<g clip-path=\"url(#clip0)\"><text", svg);
        Assert.Contains("rotate(-90)", svg);
    }

    // Word は image に直付けした clip-path を、image の x,y だけずらした位置で適用し、
    // クリップが画像から外れると塗り（半透明の塗りを埋め込んだ PNG）を表示しない。
    // 外側の g に付けたクリップは仕様どおりに適用する（2026-10-30 に Word で実測）
    [Fact]
    public void クリップ付きの画像はクリップを外側gへ退避する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.ClipDefs.Add(Square50Clip("clip0"));
        ir.Nodes.Add(new IrImage
        {
            XMm = 20, YMm = 10, WidthMm = 30, HeightMm = 20,
            PngBytes = new byte[] { 1 }, ClipId = "clip0",
        });
        var svg = SvgWriter.Write(ir);
        Assert.Matches("<g clip-path=\"url\\(#clip0\\)\">\\s*<image [^>]*/>\\s*</g>", svg);
        Assert.DoesNotMatch("<image [^>]*clip-path", svg);
    }

    static IrClipDef Square50Clip(string id) => new()
    {
        Id = id,
        Segments = new List<IrSegment>
        {
            new IrMove(0, 0), new IrLine(50, 0), new IrLine(50, 50),
            new IrLine(0, 50), new IrClose(),
        },
    };

    // 出所メタデータ（data-*）の関門。通常出力（既定）は除去し、
    // 診断用出力（includeProvenance: true）でのみ残す。現行の data-* は data-layer のみ
    // （data-aci 等は構造SVG書き出し（DB直接取得）が追加する。追加時も同じ関門を通す）。
    static IrDocument LayeredDoc()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.Add(new IrPath
        {
            Segments = DiagonalSegments(),
            Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 0.25, DataLayer = "S-WALL",
        });
        ir.Nodes.Add(new IrText { Value = "A", XMm = 5, YMm = 5, FontSizeMm = 3, DataLayer = "S-TEXT" });
        return ir;
    }

    [Fact]
    public void 通常出力は出所メタデータを除去する()
    {
        var svg = SvgWriter.Write(LayeredDoc()); // 既定 includeProvenance=false
        Assert.DoesNotContain("data-layer", svg);
    }

    [Fact]
    public void 診断出力は出所メタデータを残す()
    {
        var svg = SvgWriter.Write(LayeredDoc(), includeProvenance: true);
        Assert.Contains("data-layer=\"S-WALL\"", svg); // path 節（CommonAttrs）
        Assert.Contains("data-layer=\"S-TEXT\"", svg); // text 節
    }

    /// <summary>
    /// 通常出力(除去)が、診断出力(保持)から <paramref name="provenanceAttributePattern"/> に一致する属性だけを機械除去したものと一致する。
    /// 除去が出所メタデータ以外の属性を変えないことの確認。
    /// </summary>
    static void AssertStripChangesOnlyProvenance(IrDocument doc, string provenanceAttributePattern)
    {
        var withProvenance = SvgWriter.Write(doc, includeProvenance: true);
        var without = SvgWriter.Write(doc); // 既定=除去
        var stripped = System.Text.RegularExpressions.Regex.Replace(withProvenance, provenanceAttributePattern, "");
        Assert.Equal(stripped, without);
    }

    [Fact]
    public void 除去は出所メタデータ以外の属性を変えない()
    {
        AssertStripChangesOnlyProvenance(LayeredDoc(), " data-layer=\"[^\"]*\"");
    }

    [Fact]
    public void レイヤ別グループはInkscapeレイヤ規約付きのgで直列化する()
    {
        var doc = LayeredDoc();
        doc.LayerGroups = new List<IrLayerGroup>
        {
            new("S-WALL", 1), new("S-TEXT", 1),
        };
        var svg = SvgWriter.Write(doc);
        Assert.Contains("xmlns:inkscape=\"http://www.inkscape.org/namespaces/inkscape\"", svg);
        Assert.Contains("<g id=\"layer_0\" inkscape:groupmode=\"layer\" inkscape:label=\"S-WALL\">", svg);
        Assert.Contains("<g id=\"layer_1\" inkscape:groupmode=\"layer\" inkscape:label=\"S-TEXT\">", svg);
        // g の入れ子順: 各グループが対応ノードを包む（S-WALL の g の後に path、閉じ、次が S-TEXT）
        int gWall = svg.IndexOf("inkscape:label=\"S-WALL\"", StringComparison.Ordinal);
        int path = svg.IndexOf("<path", StringComparison.Ordinal);
        int gText = svg.IndexOf("inkscape:label=\"S-TEXT\"", StringComparison.Ordinal);
        int text = svg.IndexOf("<text", StringComparison.Ordinal);
        Assert.True(gWall < path && path < gText && gText < text);
    }

    [Fact]
    public void グループ化なしの出力にはinkscape名前空間を出さない()
    {
        var svg = SvgWriter.Write(LayeredDoc());
        Assert.DoesNotContain("inkscape", svg);
        Assert.DoesNotContain("<g id=", svg);
    }

    [Fact]
    public void グループ境界とノード数の不一致は直列化前に検出する()
    {
        var doc = LayeredDoc();   // ノード2個
        doc.LayerGroups = new List<IrLayerGroup> { new("S-WALL", 1) };  // 総和1 ≠ 2
        var ex = Assert.Throws<InvalidOperationException>(() => SvgWriter.Write(doc));
        Assert.Contains("LayerGroups", ex.Message);
    }

    [Fact]
    public void 破線パターンはdasharrayで直列化する()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(10, 0) },
            Stroke = new IrRgb(0, 0, 0), StrokeWidthMm = 0.25,
            StrokeDashMm = new[] { 1.2, 0.6 },
        });
        var svg = SvgWriter.Write(ir);
        Assert.Contains("stroke-dasharray=\"1.2 0.6\"", svg);
    }

    [Fact]
    public void 実線はdasharrayを出さない()
    {
        var svg = SvgWriter.Write(LayeredDoc());
        Assert.DoesNotContain("stroke-dasharray", svg);
    }

    static IrDocument GroupedDoc()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        var g = new IrGroup
        {
            // 平行移動(20,30)＋2倍
            Transform = new IrMatrix(2, 0, 0, 2, 20, 30),
            DataLayer = "S-BLK", DataEntity = "INSERT:TC_BLK",
        };
        g.Children.Add(new IrPath
        {
            Segments = new List<IrSegment> { new IrMove(0, 0), new IrLine(5, 0) },
            Stroke = new IrRgb(1, 0, 0), StrokeWidthMm = 0.25,
            DataColorMethod = "byblock", DataAci = 1,
        });
        ir.Nodes.Add(g);
        return ir;
    }

    [Fact]
    public void グループは変換行列付きgで直列化する()
    {
        var svg = SvgWriter.Write(GroupedDoc());
        Assert.Contains("<g transform=\"matrix(2 0 0 2 20 30)\">", svg);
        Assert.Contains("</g>", svg);
        // 子は g の内側に来る
        int gOpen = svg.IndexOf("<g transform", StringComparison.Ordinal);
        int path = svg.IndexOf("<path", StringComparison.Ordinal);
        int gClose = svg.IndexOf("</g>", StringComparison.Ordinal);
        Assert.True(gOpen < path && path < gClose);
    }

    [Fact]
    public void グループと子の出所メタデータも同じ関門を通る()
    {
        var doc = GroupedDoc();
        var svg = SvgWriter.Write(doc); // 既定=除去
        Assert.DoesNotContain("data-", svg);
        var diag = SvgWriter.Write(doc, includeProvenance: true);
        Assert.Contains("data-layer=\"S-BLK\"", diag);
        Assert.Contains("data-entity=\"INSERT:TC_BLK\"", diag);
        Assert.Contains("data-color-method=\"byblock\"", diag);
        Assert.Contains("data-aci=\"1\"", diag);
    }

    [Fact]
    public void 出所メタデータ4種の除去は他の属性を変えない()
    {
        AssertStripChangesOnlyProvenance(GroupedDoc(), " data-(layer|entity|color-method|aci)=\"[^\"]*\"");
    }

    [Fact]
    public void クリップ付きグループはクリップを外側gへ退避する()
    {
        var doc = GroupedDoc();
        doc.ClipDefs.Add(Square50Clip("c0"));
        doc.Nodes.OfType<IrGroup>().First().ClipId = "c0";
        var svg = SvgWriter.Write(doc);
        // clip-path は transform を持つ g へ直付けせず、外側の g に置く
        Assert.Contains("<g clip-path=\"url(#c0)\">", svg);
        Assert.DoesNotContain("clip-path=\"url(#c0)\" transform", svg);
        Assert.DoesNotContain("transform=\"matrix(2 0 0 2 20 30)\" clip-path", svg);
    }

    [Fact]
    public void パスの塗りなしはfill_noneになる()
    {
        var ir = new IrDocument { WidthMm = 100, HeightMm = 100 };
        ir.Nodes.Add(new IrPath
        {
            Segments = DiagonalSegments(),
            Stroke = new IrRgb(1, 0, 0),
            StrokeWidthMm = 0.25,
        });
        var svg = SvgWriter.Write(ir);
        Assert.Contains("fill=\"none\"", svg);
        Assert.Contains("stroke=\"rgb(255,0,0)\"", svg);
        Assert.Contains("stroke-width=\"0.25\"", svg);
    }
}
