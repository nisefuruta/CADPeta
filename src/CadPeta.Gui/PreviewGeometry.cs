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
    public static bool CanSave(bool busy, PreviewMode mode, bool closing) => !busy && mode == PreviewMode.Normal && !closing;
    public static bool CanCommitPreview(int candidateGeneration, int currentGeneration,
        PreviewMode requestedMode, PreviewMode currentMode) =>
        candidateGeneration == currentGeneration && requestedMode == currentMode;
    public static string InventoryBoundary(bool cropStage, bool removeBackground) =>
        cropStage ? "cropped" : removeBackground ? "background-removed" : "acquired";
    public static bool SameInventory(IReadOnlyList<CadPeta.Transforms.ColorUse> left,
        IReadOnlyList<CadPeta.Transforms.ColorUse> right) => left.Count == right.Count && left.SequenceEqual(right);
    public static (bool ApplyCrop, bool ApplyColor) RequestFlags(PreviewMode mode) => mode switch
    {
        PreviewMode.PreparingCrop or PreviewMode.SelectCrop => (false, true),
        PreviewMode.PreparingPickColor or PreviewMode.PickColor => (true, false),
        _ => (true, true),
    };
}
