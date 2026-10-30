// グラデーションハッチのラスタ化結果からのIR組み立てと診断記録。特定のCAD製品APIに
// 依存しない共有計算のため、4製品（AutoCAD/ARES/BricsCAD/IJCAD）の取得アダプタ間で共有し
// 単体テスト対象にする。
//
// stops・境界・スタイル（不透明度・色指定方法・ACI・出所レイヤ／エンティティ名）の抽出は
// CAD SDK 型（Hatch 等）に触れるため、呼び出し側（各製品の IrAcquirer）の責務のまま残す。
// フォールバック時の塗りパス（IrPath）組み立ても、出所メタデータの取得元が同じ SDK 型のため
// 呼び出し側のデリゲート（emitFallbackPath）へ委ねる。それ以外（ラスタ化呼び出し・画素予算
// 消費・IrImage/IrGroup 組み立て・FallbackKind から診断カテゴリへの写像・診断記録）は
// 4製品で完全に同一の意味論のため、ここへ一本化する。
using System;
using System.Collections.Generic;
using CadPeta.Acquire.Common;
using CadPeta.Ir;

namespace CadPeta.Acquire.Raster;

public static class GradientEmitter
{
    /// <summary>ラスタ成功時にIRへ書き出す出所メタデータ（data-*属性相当）。
    /// いずれもCore型のみで構成され、CAD SDK型を含まない。</summary>
    public readonly record struct Metadata(string? DataLayer, string? DataEntity, string? ColorMethod, int? Aci);

    /// <summary>
    /// グラデーションハッチ1件を処理する: ラスタ化 → 画素予算消費 → IR組み立て（成功時）／
    /// 塗りパスへの縮退（縮退時）→ 診断記録、までを一本化する。
    /// 縮退時の塗りパス組み立ては emitFallbackPath（呼び出し側が SDK 型から構築する）へ委ねる。
    /// </summary>
    /// <param name="request">ラスタ化要求（stops・境界は呼び出し側が抽出済み）。</param>
    /// <param name="budget">取得1回で共有する画素予算。</param>
    /// <param name="metadata">ラスタ成功時に IrImage へ載せる出所メタデータ。</param>
    /// <param name="diagTarget">診断の対象文字列（製品名・ハンドル・レイヤ・要求DPI等、呼び出し側で組み立て済み）。</param>
    /// <param name="diag">診断記録先。</param>
    /// <param name="sink">IRノードの追加先。</param>
    /// <param name="emitFallbackPath">縮退時に塗りパスを sink へ追加するコールバック（色を受け取る）。
    /// 定義不正でスキップする場合（SkippedInvalid）は呼ばれない（挙動は移設前と同一）。</param>
    public static void Emit(GradientHatchRequest request, GradientRasterBudget budget, Metadata metadata,
                            string diagTarget, IrDiagnostics diag, List<IrNode> sink,
                            Action<IrRgb> emitFallbackPath)
    {
        GradientHatchResult result = GradientRasterizer.Rasterize(request);
        if (result.Status == GradientHatchStatus.Rasterized && budget.TryConsume(result.PixelCount))
        {
            EmitRasterized(result, request.DocumentFromLocal, metadata, diagTarget, diag, sink, emitFallbackPath);
            return;
        }

        // ラスタ化には成功したが予算消費に失敗した場合（他要素との合算で不足）を含め、
        // ここへ到達するのは「画像を出さない」全ケース（縮退 or スキップ）。
        if (result.Status is GradientHatchStatus.Rasterized or GradientHatchStatus.SolidFallback)
            emitFallbackPath(result.SolidColor);

        string category = result.Status == GradientHatchStatus.Rasterized
            ? AcquireDiagCategories.GradientBudgetFallback
            : result.FallbackKind switch
            {
                GradientFallbackKind.UnknownShape => AcquireDiagCategories.GradientShapeFallback,
                GradientFallbackKind.PixelBudget => AcquireDiagCategories.GradientBudgetFallback,
                _ => AcquireDiagCategories.GradientInvalidFallback,
            };
        // 予算消費失敗時（Rasterized）は FallbackReason が常に null（Rasterizer が理由を持たない）。
        // 理由があれば付す・なければ対象文字列のみ、で4製品とも同一の組み立てにする。
        diag.Add(category, string.IsNullOrEmpty(result.FallbackReason)
            ? diagTarget : diagTarget + " " + result.FallbackReason);
    }

    static void EmitRasterized(GradientHatchResult result, IrMatrix documentFromLocal, Metadata metadata,
                                string diagTarget, IrDiagnostics diag, List<IrNode> sink,
                                Action<IrRgb> emitFallbackPath)
    {
        var image = new IrImage
        {
            XMm = result.XMm, YMm = result.YMm, WidthMm = result.WidthMm, HeightMm = result.HeightMm,
            PngBytes = result.PngBytes,
            DataLayer = metadata.DataLayer, DataEntity = metadata.DataEntity,
            DataColorMethod = metadata.ColorMethod, DataAci = metadata.Aci,
        };
        if (documentFromLocal.IsIdentity)
            sink.Add(image);
        else if (documentFromLocal.TryInverse(out IrMatrix inverse))
            sink.Add(new IrGroup
            {
                Transform = inverse, DataLayer = image.DataLayer, DataEntity = image.DataEntity,
                DataColorMethod = image.DataColorMethod, DataAci = image.DataAci,
                Children = { image },
            });
        else
        {
            // 画素予算はすでに消費済み（ロールバックしない。移設前と同じ挙動）
            emitFallbackPath(result.SolidColor);
            diag.Add(AcquireDiagCategories.GradientInvalidFallback, diagTarget + " 特異変換");
            return;
        }
        diag.Add(AcquireDiagCategories.GradientRasterized,
            diagTarget + FormattableString.Invariant($" 実={result.ActualDpi:0.##}dpi {result.WidthPx}x{result.HeightPx}px"),
            result.ActualDpi);
        if (result.DpiReductionReason != null)
            diag.Add(AcquireDiagCategories.GradientDpiReduced, diagTarget + " " + result.DpiReductionReason);
    }
}
