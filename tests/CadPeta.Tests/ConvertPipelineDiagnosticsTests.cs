// 診断モードの単体テスト。ApplyWithDiagnostics が
//  - 段0（取得直後）＋各適用段の後、という境界単位でダンプを回すこと
//  - 各境界の IR 状態が段の効果を反映し、diag の件数と保存則が一致すること
//  - ファイル名規約（段番号ゼロ埋め・段名）
// を検査する。ダンプ本文の SVG 妥当性（ブラウザ表示）は CLI 実物検証で確認する。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.UseCases;
using Xunit;
using static CadPeta.Tests.IrTestBuilders;

namespace CadPeta.Tests;

public class ConvertPipelineDiagnosticsTests
{
    static IrPath Bg(double w, double h)
        => new()
        {
            Segments = new List<IrSegment>
            {
                new IrMove(0, 0), new IrLine(w, 0), new IrLine(w, h),
                new IrLine(0, h), new IrClose(),
            },
            Fill = new IrRgb(1, 1, 1),
        };

    // 各境界の (段番号, 段名, その時点のノード数) を記録するシンク。
    // IR は破壊的に変わるため、参照ではなく件数をその場で確定する。
    static Action<int, string, IrDocument> Recorder(List<(int Ordinal, string Name, int NodeCount)> log)
        => (ordinal, name, ir) => log.Add((ordinal, name, ir.Nodes.Count));

    [Fact]
    public void 変換段がなければ段0のみをダンプする()
    {
        var ir = A3Page(Line(10, 10, 90, 90));
        var boundaries = new List<(int, string, int)>();

        ConvertPipeline.ApplyWithDiagnostics(
            ir, new ConvertOptions { RemoveBackground = false }, new IrDiagnostics(),
            Recorder(boundaries));

        Assert.Single(boundaries);
        Assert.Equal((0, "acquired", 1), boundaries[0]);
    }

    [Fact]
    public void 背景除去の前後をダンプし件数差が除去件数と一致する()
    {
        var ir = A3Page(Bg(420, 297), Line(10, 10, 90, 90));
        var boundaries = new List<(int Ordinal, string Name, int NodeCount)>();
        var diag = new IrDiagnostics();

        ConvertPipeline.ApplyWithDiagnostics(
            ir, new ConvertOptions { RemoveBackground = true }, diag, Recorder(boundaries));

        Assert.Equal(2, boundaries.Count);
        Assert.Equal((0, "acquired", 2), (boundaries[0].Ordinal, boundaries[0].Name, boundaries[0].NodeCount));
        Assert.Equal((1, "background-removed", 1), (boundaries[1].Ordinal, boundaries[1].Name, boundaries[1].NodeCount));
        // 保存則: 段0→段1 のノード減少数 = 背景除去の診断件数
        int removedByCount = boundaries[0].NodeCount - boundaries[1].NodeCount;
        int removedByDiag = diag.Entries.Count(e => e.Type.StartsWith("背景矩形の除去"));
        Assert.Equal(removedByDiag, removedByCount);
        Assert.Equal(1, removedByCount);
    }

    [Fact]
    public void クロップの前後をダンプし間引き数が診断件数と一致する()
    {
        var inside = Line(20, 20, 80, 80);
        var outside = Line(200, 200, 300, 250);
        var ir = A3Page(inside, outside);
        var boundaries = new List<(int Ordinal, string Name, int NodeCount)>();
        var diag = new IrDiagnostics();

        ConvertPipeline.ApplyWithDiagnostics(
            ir,
            new ConvertOptions { RemoveBackground = false, CropRectMm = new RectMm(0, 0, 100, 100) },
            diag, Recorder(boundaries));

        Assert.Equal(2, boundaries.Count);
        Assert.Equal((0, "acquired"), (boundaries[0].Ordinal, boundaries[0].Name));
        Assert.Equal((1, "cropped"), (boundaries[1].Ordinal, boundaries[1].Name));
        // 保存則: 段0→段1 の間引き数 = クロップの診断件数
        int droppedByCount = boundaries[0].NodeCount - boundaries[1].NodeCount;
        int droppedByDiag = diag.Entries.Count(e => e.Type.StartsWith("クロップ: 範囲外ノードの間引き"));
        Assert.Equal(droppedByDiag, droppedByCount);
        Assert.Equal(1, droppedByCount);
    }

    [Fact]
    public void 両段が有効なら段0と各段後の3境界を順にダンプする()
    {
        var ir = A3Page(Bg(420, 297), Line(20, 20, 80, 80), Line(200, 200, 300, 250));
        var boundaries = new List<(int Ordinal, string Name, int NodeCount)>();

        ConvertPipeline.ApplyWithDiagnostics(
            ir,
            new ConvertOptions { RemoveBackground = true, CropRectMm = new RectMm(0, 0, 100, 100) },
            new IrDiagnostics(), Recorder(boundaries));

        Assert.Equal(new[] { "acquired", "background-removed", "cropped" },
                     boundaries.Select(b => b.Name).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, boundaries.Select(b => b.Ordinal).ToArray());
        // 背景除去で 3→2、クロップで範囲外1件を間引き 2→1
        Assert.Equal(new[] { 3, 2, 1 }, boundaries.Select(b => b.NodeCount).ToArray());
    }

    [Fact]
    public void 段0のダンプは変換前の初期IRを表す()
    {
        var ir = A3Page(Bg(420, 297), Line(10, 10, 90, 90));
        string? stage0Svg = null;

        // 診断シンクは出所メタデータを残す（合成ルートと同じ includeProvenance: true）
        ConvertPipeline.ApplyWithDiagnostics(
            ir, new ConvertOptions { RemoveBackground = true }, new IrDiagnostics(),
            (ordinal, _, doc) => { if (ordinal == 0) stage0Svg = SvgWriter.Write(doc, includeProvenance: true); });

        // 段0は背景除去前 → 背景の白塗り矩形（fill="rgb(255,255,255)"）が残っている
        Assert.NotNull(stage0Svg);
        Assert.Contains("rgb(255,255,255)", stage0Svg);
    }

    [Fact]
    public void 診断ダンプは出所メタデータを残し通常出力は除去する()
    {
        // 通常出力は data-* を除去し、診断用出力でのみ残す。診断シンク（保持）と
        // 通常出力（除去）の分岐を、合成ルートと同じ呼び方で検査する。
        var line = Line(20, 20, 80, 80);
        line.DataLayer = "S-WALL";
        var ir = A3Page(line);

        string? diagDump = null;
        ConvertPipeline.ApplyWithDiagnostics(
            ir, new ConvertOptions { RemoveBackground = false }, new IrDiagnostics(),
            (ordinal, _, doc) => { if (ordinal == 0) diagDump = SvgWriter.Write(doc, includeProvenance: true); });

        Assert.NotNull(diagDump);
        Assert.Contains("data-layer=\"S-WALL\"", diagDump);        // 診断ダンプ=保持
        Assert.DoesNotContain("data-layer", SvgWriter.Write(ir));   // 通常出力=除去（既定）
    }

    [Fact]
    public void ファイル名は段番号ゼロ埋めと段名を含む()
    {
        Assert.Equal("draw.stage00.acquired.svg", DiagnosticDump.FileName("draw", 0, "acquired"));
        Assert.Equal("draw.stage01.background-removed.svg", DiagnosticDump.FileName("draw", 1, "background-removed"));
        Assert.Equal("draw.stage02.cropped.svg", DiagnosticDump.FileName("draw", 2, "cropped"));
    }
}
