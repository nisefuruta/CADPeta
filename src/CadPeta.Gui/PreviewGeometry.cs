using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;

namespace CadPeta.Gui;

internal static class PreviewGeometry
{
    public static bool TryControlToImagePixel(PointF control, Size box, Size image, out PointF pixel)
    {
        pixel = default;
        if (!TryLayout(box, image, out float scale, out float ox, out float oy)) return false;
        float x = (control.X - ox) / scale, y = (control.Y - oy) / scale;
        if (x < 0 || y < 0 || x > image.Width || y > image.Height) return false;
        pixel = new PointF(x, y); return true;
    }

    public static PointF ControlToClampedImagePixel(PointF control, Size box, Size image)
    {
        if (!TryLayout(box, image, out float scale, out float ox, out float oy)) return default;
        return new PointF(Math.Clamp((control.X - ox) / scale, 0, image.Width),
            Math.Clamp((control.Y - oy) / scale, 0, image.Height));
    }

    public static PointF ImageToPageMm(PointF pixel, Size image, double widthMm, double heightMm) =>
        new((float)(pixel.X / image.Width * widthMm), (float)(pixel.Y / image.Height * heightMm));

    public static RectangleF ImageSelectionToControl(PointF a, PointF b, Size box, Size image)
    {
        TryLayout(box, image, out float scale, out float ox, out float oy);
        float x0 = Math.Min(a.X, b.X) * scale + ox, y0 = Math.Min(a.Y, b.Y) * scale + oy;
        float x1 = Math.Max(a.X, b.X) * scale + ox, y1 = Math.Max(a.Y, b.Y) * scale + oy;
        return RectangleF.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>画面の長さを、表示している画像の画素の長さ（1 以上に切り上げ）にする。</summary>
    public static int ControlLengthToImagePixels(double length, Size box, Size image) =>
        TryLayout(box, image, out float scale, out _, out _) ? Math.Max(1, (int)Math.Ceiling(length / scale)) : 1;

    public static Point PixelIndex(PointF pixel, Size image) => new(
        Math.Clamp((int)Math.Floor(pixel.X), 0, image.Width - 1),
        Math.Clamp((int)Math.Floor(pixel.Y), 0, image.Height - 1));

    static bool TryLayout(Size box, Size image, out float scale, out float ox, out float oy)
    {
        scale = ox = oy = 0;
        if (box.Width <= 0 || box.Height <= 0 || image.Width <= 0 || image.Height <= 0) return false;
        scale = Math.Min((float)box.Width / image.Width, (float)box.Height / image.Height);
        ox = (box.Width - image.Width * scale) / 2; oy = (box.Height - image.Height * scale) / 2;
        return true;
    }
}

internal enum PreviewMode { Normal, PreparingCrop, SelectCrop, PreparingPickColor, PickColor }

internal static class PreviewPolicy
{
    // プレビューは画面で見るだけなので、保存の既定（CadPetaSettings.DefaultPngDpi）より低い解像度で描いて速くする
    public const double PreviewDpi = 150;
    public static bool CanSave(bool busy, PreviewMode mode, bool closing) => !busy && mode == PreviewMode.Normal && !closing;
    public static bool CanCommitPreview(int candidateGeneration, int currentGeneration,
        PreviewMode requestedMode, PreviewMode currentMode) =>
        candidateGeneration == currentGeneration && requestedMode == currentMode;
    public static string InventoryBoundary(bool cropStage, bool removeBackground) =>
        cropStage ? "cropped" : removeBackground ? "background-removed" : "acquired";
    public static bool SameInventory(IReadOnlyList<CadPeta.Transforms.ColorUse> left,
        IReadOnlyList<CadPeta.Transforms.ColorUse> right) => left.Count == right.Count && left.SequenceEqual(right);
    /// <summary>
    /// 範囲の指定・スポイトの準備のプレビューが取り消されたときに、モードを通常に戻すか。新しい作り直し（世代が進んだ）か、
    /// 作り直しの予約（<paramref name="refreshPending"/>、設定を変えた直後の待ち）があれば、そちらが同じモードのプレビューを作るので戻さない。
    /// </summary>
    public static bool EndModeOnCancel(bool closing, PreviewMode requested, PreviewMode current,
        int generation, int currentGeneration, bool refreshPending) =>
        !closing && requested is PreviewMode.PreparingCrop or PreviewMode.PreparingPickColor && current == requested
        && generation == currentGeneration && !refreshPending;

    // モードごとのプレビューの作り方（1 つの表）。表示した後のモード、範囲の切り出し、色の変換を使うか。
    // 範囲の指定はページ全体を（色の変換をして）、スポイトは切り出した範囲を元の色で描く。範囲の指定・スポイトのモードの中で設定を変えて
    // 作り直したときも、そのモードのまま表示する（そのプレビューは範囲や色の変換を外して描いているので、通常のモードとして表示しない）
    static (PreviewMode After, bool ApplyCrop, bool ApplyColor) Plan(PreviewMode mode) => mode switch
    {
        PreviewMode.PreparingCrop or PreviewMode.SelectCrop => (PreviewMode.SelectCrop, false, true),
        PreviewMode.PreparingPickColor or PreviewMode.PickColor => (PreviewMode.PickColor, true, false),
        _ => (PreviewMode.Normal, true, true),
    };

    /// <summary>プレビューを表示した後のモード。</summary>
    public static PreviewMode ModeAfterPreview(PreviewMode requested) => Plan(requested).After;

    /// <summary>スポイトの色番号の画像を作るか（スポイトの準備と、スポイトのモードの中での作り直し）。</summary>
    public static bool NeedsPickMap(PreviewMode requested) => Plan(requested).After == PreviewMode.PickColor;

    public static (bool ApplyCrop, bool ApplyColor) RequestFlags(PreviewMode mode)
    {
        var plan = Plan(mode);
        return (plan.ApplyCrop, plan.ApplyColor);
    }
}
