using System;
using System.Collections.Generic;
using CadPeta.Acquire.Common;
using CadPeta.Acquire.Raster;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

// GradientEmitter（4製品の取得アダプタが共有する、ラスタ化結果からのIR組み立て・診断記録）の単体検証。
// ラスタ化自体の数値的な正しさは GradientRasterTests が担うため、ここでは「ラスタ化結果の分岐ごとに
// 4製品が同一の診断カテゴリ・文言・IR組み立てになるか」だけを検証する（重複を作らない）。
public class GradientEmitterTests
{
    [Fact]
    public void ラスタ化成功時はIrImageを追加し実DPI付きで記録する()
    {
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(1_000_000);
        bool fallbackCalled = false;

        GradientEmitter.Emit(GradientTestRequests.Basic(), budget,
            new GradientEmitter.Metadata("0", "HATCH", "aci", 3),
            "AutoCAD HATCH 1A layer=0 LINEAR 要求=10dpi", diag, sink, _ => fallbackCalled = true);

        Assert.False(fallbackCalled);
        var image = Assert.IsType<IrImage>(Assert.Single(sink));
        Assert.Equal("0", image.DataLayer);
        Assert.Equal("HATCH", image.DataEntity);
        Assert.Equal("aci", image.DataColorMethod);
        Assert.Equal(3, image.DataAci);
        Assert.Equal(25.4, image.WidthMm, 3);
        Assert.Equal(25.4, image.HeightMm, 3);
        var entry = Assert.Single(diag.Entries, e => e.Type == AcquireDiagCategories.GradientRasterized);
        Assert.Contains("実=10dpi 10x10px", entry.Target);
        Assert.Equal(10.0, entry.Value);
        Assert.Equal(1_000_000 - 100, budget.RemainingPixels);
    }

    [Fact]
    public void 変換ありの成功時はIrGroupへ逆行列を積んで格納する()
    {
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(1_000_000);

        GradientEmitter.Emit(GradientTestRequests.Basic() with { DocumentFromLocal = new IrMatrix(2, 0, 0, 2, 5, 5) },
            budget, new GradientEmitter.Metadata("L", "HATCH", "truecolor", null),
            "target", diag, sink, _ => { });

        var group = Assert.IsType<IrGroup>(Assert.Single(sink));
        Assert.Single(group.Children);
        Assert.IsType<IrImage>(group.Children[0]);
        Assert.Equal("L", group.DataLayer);
        Assert.Equal("truecolor", group.DataColorMethod);
    }

    [Fact]
    public void 未知形状は代表色フォールバックと形状縮退診断を記録する()
    {
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(1_000_000);
        IrRgb? fallbackColor = null;

        GradientEmitter.Emit(GradientTestRequests.Basic() with { GradientName = "UNKNOWN" }, budget,
            new GradientEmitter.Metadata("0", "HATCH", "aci", 1),
            "AutoCAD HATCH 1A layer=0 UNKNOWN 要求=10dpi", diag, sink, color => fallbackColor = color);

        Assert.Empty(sink); // IR組み立ては呼び出し側のfallbackPath委譲。Emitter自身はsinkへ追加しない
        Assert.NotNull(fallbackColor);
        var entry = Assert.Single(diag.Entries);
        Assert.Equal(AcquireDiagCategories.GradientShapeFallback, entry.Type);
        Assert.Contains("未知のGradientName", entry.Target);
        Assert.StartsWith("AutoCAD HATCH 1A layer=0 UNKNOWN 要求=10dpi ", entry.Target);
    }

    [Fact]
    public void 最低DPI未満の画素予算は代表色フォールバックと予算縮退診断を記録する()
    {
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(1_000_000);
        bool fallbackCalled = false;

        GradientEmitter.Emit(GradientTestRequests.Basic() with { RequestedDpi = 100, AvailablePixels = 4, MinimumDpi = 10 },
            budget, new GradientEmitter.Metadata(null, null, null, null),
            "target", diag, sink, _ => fallbackCalled = true);

        Assert.True(fallbackCalled);
        var entry = Assert.Single(diag.Entries);
        Assert.Equal(AcquireDiagCategories.GradientBudgetFallback, entry.Type);
        Assert.Contains("最低", entry.Target);
        Assert.Equal(1_000_000, budget.RemainingPixels); // 縮退のため未消費
    }

    [Fact]
    public void ラスタ化後の予算超過は理由なしで予算縮退診断を記録する()
    {
        // 実運用ではAvailablePixelsはbudget.RemainingPixelsから都度算出するため両者はズレないが、
        // TryConsume失敗時の防御分岐（呼び出し側の計算誤り・将来の decouple への保険）は
        // 診断ドリフトの実例そのもの（旧AcadCADは理由なしtarget、旧ARES系は
        // FallbackReason=nullを連結した末尾余剰空白）だったため、あえてズレを作って単体検証する。
        // Rasterizer は Rasterized 結果に FallbackReason を持たせないため、
        // 統一後は4製品とも「対象文字列のみ」で診断が一致する（余剰空白を作らない）。
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(50); // 100px必要だが50pxしか残っていない
        bool fallbackCalled = false;

        GradientEmitter.Emit(GradientTestRequests.Basic(), budget,
            new GradientEmitter.Metadata("0", "HATCH", "aci", 1),
            "AutoCAD HATCH 1A layer=0 LINEAR 要求=10dpi", diag, sink, _ => fallbackCalled = true);

        Assert.True(fallbackCalled);
        Assert.Empty(sink);
        var entry = Assert.Single(diag.Entries);
        Assert.Equal(AcquireDiagCategories.GradientBudgetFallback, entry.Type);
        Assert.Equal("AutoCAD HATCH 1A layer=0 LINEAR 要求=10dpi", entry.Target);
        Assert.Equal(50, budget.RemainingPixels); // 消費に失敗しているので残量は変化しない
    }

    [Fact]
    public void 色ストップ不足は縮退パスを描かず定義不正診断だけを記録する()
    {
        var diag = new IrDiagnostics();
        var sink = new List<IrNode>();
        var budget = new GradientRasterBudget(1_000_000);
        bool fallbackCalled = false;

        GradientEmitter.Emit(GradientTestRequests.Basic() with { Stops = Array.Empty<GradientStop>() }, budget,
            new GradientEmitter.Metadata("0", "HATCH", "aci", 1),
            "AutoCAD HATCH 1A layer=0 LINEAR 要求=10dpi", diag, sink, _ => fallbackCalled = true);

        // SkippedInvalid はエンティティごと描画しない既存挙動（移設前と同一）。
        // 塗りパスへの縮退すら行わないため、fallbackPathは呼ばれない。
        Assert.False(fallbackCalled);
        Assert.Empty(sink);
        var entry = Assert.Single(diag.Entries);
        Assert.Equal(AcquireDiagCategories.GradientInvalidFallback, entry.Type);
        Assert.Contains("色ストップ", entry.Target);
    }

}
