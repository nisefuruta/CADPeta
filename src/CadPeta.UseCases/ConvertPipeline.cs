// IR変換層のパイプライン。変換の適用順は固定:
// 1.背景除去（PDF印刷経路由来のみ）→ 2.クロップ → 3.線幅正規化（最細線の固定幅置換として取得側で
// 充足済み。変換段が要るときにここへ追加）→ 4.色変換 → 5.文字アウトライン化（色変換の後=
// 変換済みの色を輪郭へ引き継ぐ）→ 6.レイヤ別グループ化（構造を変えるため最終段）
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;

namespace CadPeta.UseCases;

public sealed record ConvertOptions
{
    /// <summary>背景矩形の除去（既定で有効）。</summary>
    public bool RemoveBackground { get; init; } = true;

    /// <summary>クロップ範囲（mm、ページ左上原点）。null なら全面。</summary>
    public RectMm? CropRectMm { get; init; }

    /// <summary>クロップ範囲へ付ける余白（mm）。</summary>
    public double CropMarginMm { get; init; }

    /// <summary>背景除去後の描画要素外接矩形へクロップする。明示 CropRectMm とは排他。</summary>
    public bool ContentCrop { get; init; }

    /// <summary>色変換モード（既定 None=変換なし）。</summary>
    public ColorMode ColorMode { get; init; } = ColorMode.None;

    /// <summary>色の一括置換。モード適用より先に評価する。</summary>
    public IReadOnlyList<ColorReplacement> ColorReplacements { get; init; } =
        Array.Empty<ColorReplacement>();

    /// <summary>文字アウトライン化（全 text を輪郭の path へ展開。環境非依存の見た目を
    /// 優先する場合の設定。既定オフ=フォント保持）。</summary>
    public bool TextOutline { get; init; }

    /// <summary>文字アウトライン化に使うグリフ輪郭ポートの実装。TextOutline=true のとき
    /// 合成ルートが注入する（ユースケース層は実装へ依存しない）。</summary>
    public IGlyphOutliner? TextOutliner { get; init; }

    /// <summary>レイヤ別グループ化（z順が変わり見た目が崩れ得るため既定オフ）。</summary>
    public bool GroupByLayer { get; init; }
}

public static class ConvertPipeline
{
    /// <summary>変換層の1段（冒頭コメントの固定順の各段）。段名は診断ダンプの識別子を兼ねる。</summary>
    public sealed record Stage(string Name, Action<IrDocument, IrDiagnostics> Apply);

    // 適用する段を冒頭コメントの固定順で列挙する。オプションで無効な段は列挙しないため
    // （--keep-bg で背景除去なし、--crop 無指定でクロップなし等）、既定はこれまでと同一の
    // 段構成＝無回帰。段を足すとここだけで本処理・診断ダンプの両方へ反映される。
    static IEnumerable<Stage> Stages(ConvertOptions options)
    {
        if (options.ContentCrop && options.CropRectMm != null)
            throw new ArgumentException("内容クロップと明示クロップは同時に指定できない");
        if (options.RemoveBackground)
            yield return new Stage("background-removed",
                (ir, diag) => BackgroundRemover.Apply(ir, diag));
        if (options.CropRectMm is { } rect)
            yield return new Stage("cropped",
                (ir, diag) => CropTransform.Apply(ir, rect, options.CropMarginMm, diag));
        if (options.ContentCrop)
            yield return new Stage("content-cropped",
                (ir, diag) => ContentCropTransform.Apply(ir, options.CropMarginMm, diag));
        if (options.ColorMode != ColorMode.None || options.ColorReplacements.Count > 0)
            yield return new Stage("color-converted",
                (ir, diag) => ColorTransform.Apply(ir, options.ColorMode, options.ColorReplacements, diag));
        if (options.TextOutline)
        {
            var outliner = options.TextOutliner
                ?? throw new InvalidOperationException(
                    "文字アウトライン化が有効なのにグリフ輪郭ポート（TextOutliner）が注入されていない" +
                    "（合成ルートの結線漏れ）");
            yield return new Stage("text-outlined",
                (ir, diag) => TextOutlineTransform.Apply(ir, outliner, diag));
        }
        if (options.GroupByLayer)
            yield return new Stage("layer-grouped",
                (ir, diag) => LayerGroupTransform.Apply(ir, diag));
    }

    public static void Apply(IrDocument ir, ConvertOptions options, IrDiagnostics diag,
        CancellationToken cancellationToken = default)
    {
        foreach (var stage in Stages(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage.Apply(ir, diag);
        }
    }

    /// <summary>
    /// 診断モード: 変換層の各段の前後で IR を境界ダンプへ渡す。
    /// 段0＝取得直後（変換前の初期IR）、以降は各段の適用後を渡す。前段の after は次段の
    /// before と同一 IR のため、境界単位のダンプ（N段→N+1回）で重複なく「各段の前後」を満たす。
    /// onBoundary は境界1件（段番号・段名・その時点の IR）を受け取る。直列化とファイル
    /// 書き出しは合成ルート（CLI・プラグイン）が担い、内核・ユースケース層に IO を置かない。
    /// onBoundary は本処理を巻き込まないよう自身の失敗を捕捉・ログする責務を負う
    /// （診断は付随機能。ここで例外を握りつぶすと理由を残せないため、伝播責任を合成ルートへ委ねる）。
    /// </summary>
    public static void ApplyWithDiagnostics(
        IrDocument ir, ConvertOptions options, IrDiagnostics diag,
        Action<int, string, IrDocument> onBoundary,
        CancellationToken cancellationToken = default)
    {
        int ordinal = 0;
        cancellationToken.ThrowIfCancellationRequested();
        onBoundary(ordinal, "acquired", ir);
        foreach (var stage in Stages(options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            stage.Apply(ir, diag);
            onBoundary(++ordinal, stage.Name, ir);
        }
    }
}
