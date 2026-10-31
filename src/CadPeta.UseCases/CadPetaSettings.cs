using System;
using System.Globalization;
using System.IO;
using CadPeta.Geometry;

namespace CadPeta.UseCases;

/// <summary>
/// CADPeta の設定（利用者ごとに 1 つ）。各項目の既定値・範囲・検証はこの型だけが持ち、PETA・PETAOPT・CLI・GUI・PNG の書き出しは
/// すべてこれを使う（同じ既定値や範囲をほかの場所に書かない）。読み書きは <see cref="CadPetaSettingsStore"/>。
/// クリップボードへの EMF の登録は、EMF のファイルを出すかどうかにかかわらず行う。
/// </summary>
public sealed record CadPetaSettings(string OutputFolder, FileNameTemplate FileName, ArtifactKindSet Formats, double PngDpi)
{
    /// <summary>設定のファイルの形の版。項目の意味を変えたら上げる。</summary>
    public const int SchemaVersion = 1;

    /// <summary>既定の出力先。成果物を図面のファイルと分けるため、図面のフォルダの下の CADPeta に出す。</summary>
    public const string DefaultOutputFolder = @".\CADPeta";

    /// <summary>PNG の解像度の既定値（DPI。利用者の決定、2026-10-28）。</summary>
    public const double DefaultPngDpi = 300;

    /// <summary>
    /// PNG の解像度の上限（DPI）。A0 の長い辺（1,189 mm）が画像の 1 辺の上限（<see cref="RasterLimits.MaxDimensionPx"/>）に収まる
    /// 解像度（約 699 DPI）より低い、切りのよい値。下限は <see cref="RasterLimits.MinDpi"/>。大きな用紙では、画素数の上限
    /// （<see cref="PngSize.MaxPixels"/>）のため、書くときにこれより低い解像度に下げることがある（A0 は約 416 DPI）。
    /// </summary>
    public const double MaxPngDpi = 600;

    /// <summary>既定: EMF だけを、図面のフォルダの CADPeta に、今までと同じ名前で出す。</summary>
    public static CadPetaSettings Default { get; } =
        new(DefaultOutputFolder, FileNameTemplate.Default, ArtifactKindSet.Of(ArtifactKind.Emf), DefaultPngDpi);

    /// <summary>PNG の解像度の入力の文字（前後の空白は除く）を数にする。数でないか範囲の外なら理由を返す（使えれば null）。</summary>
    public static string? ParsePngDpi(string text, out double dpi)
    {
        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out dpi)) dpi = double.NaN;
        return ValidatePngDpi(dpi);
    }

    /// <summary>PNG の解像度が範囲の中か。範囲の外なら理由を返す（中なら null）。</summary>
    public static string? ValidatePngDpi(double dpi) =>
        Numbers.IsFinite(dpi) && dpi >= RasterLimits.MinDpi && dpi <= MaxPngDpi
            ? null
            : MessageCatalogs.Current.Format("settings.png_dpi_range", RasterLimits.MinDpi, MaxPngDpi);

    /// <summary>
    /// 出力先として使えるか。相対パス（図面のフォルダが起点）か、ドライブ名か \\ で始まる完全なパスだけを受け付ける。
    /// \ だけで始まるパスと C:abc のような形は、起点が分かりにくいので受け付けない。使えなければ理由を返す（使えれば null）。
    /// </summary>
    public static string? ValidateOutputFolder(string? folder)
    {
        var catalog = MessageCatalogs.Current;
        if (string.IsNullOrWhiteSpace(folder))
            return catalog.Format("settings.folder_empty");
        if (folder!.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || folder.IndexOfAny(new[] { '*', '?', '"', '<', '>', '|' }) >= 0)
            return catalog.Format("settings.folder_invalid_chars", folder);
        // コロンはドライブ名の位置（C:\）だけに使える。ほかの位置のコロンはフォルダの名前に使えない
        if (folder.IndexOf(':', IsFullyQualified(folder) && folder[1] == ':' ? 2 : 0) >= 0)
            return catalog.Format("settings.folder_invalid_chars", folder);
        if (Path.IsPathRooted(folder) && !IsFullyQualified(folder))
            return catalog.Format("settings.folder_partially_rooted", folder);
        return null;
    }

    /// <summary>
    /// 出力先の実際のフォルダ。相対パスは <paramref name="drawingDir"/>（図面のフォルダ）を起点にする。保存していない図面
    /// （<paramref name="drawingDir"/> が null）で相対パスなら、起点が無いので null。
    /// </summary>
    public string? ResolveOutputFolder(string? drawingDir)
    {
        if (IsFullyQualified(OutputFolder))
            return Path.GetFullPath(OutputFolder);
        return drawingDir == null ? null : Path.GetFullPath(Path.Combine(drawingDir, OutputFolder));
    }

    internal static bool IsFullyQualified(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) ||
        (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
}
