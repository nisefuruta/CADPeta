using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CadPeta.Geometry;
using CadPeta.Ir;

namespace CadPeta.Acquire.Raster;

public static class GradientRasterizer
{
    public static GradientHatchResult Rasterize(GradientHatchRequest request)
    {
        GradientStop[] stops;
        try { stops = ValidateStops(request.Stops); }
        catch (ArgumentException ex) { return Invalid(ex.Message); }
        IrRgb representative = Interpolate(stops, .5);

        if (!GradientSemantics.IsKnown(request.GradientName))
            return Fallback(representative, GradientFallbackKind.UnknownShape,
                $"未知のGradientName: {request.GradientName}");
        if (!Numbers.IsFinite(request.AngleRadians) || !Numbers.IsFinite(request.Shift) || request.Shift < 0 || request.Shift > 1)
            return Fallback(representative, GradientFallbackKind.InvalidDefinition, "角度またはShiftが不正");
        if (!Numbers.IsFinite(request.Opacity) || request.Opacity < 0 || request.Opacity > 1)
            return Invalid("不透明度は0..1の有限値が必要");
        if (!(request.RequestedDpi > 0) || !(request.MinimumDpi > 0)
            || request.MaxDimensionPx <= 0 || request.AvailablePixels <= 0)
            return Fallback(representative, GradientFallbackKind.InvalidDefinition, "DPIまたは画素予算が不正");
        if (!request.DocumentFromLocal.TryInverse(out IrMatrix localFromDocument))
            return Fallback(representative, GradientFallbackKind.InvalidDefinition, "DocumentFromLocalが特異行列");
        if (!TryBounds(request.Boundary, IrMatrix.Identity, out Bounds local)
            || !TryBounds(request.Boundary, request.DocumentFromLocal, out Bounds document))
            return Invalid("境界が空、非有限、または面積0");

        double dpi = request.RequestedDpi;
        string? reduction = null;
        if (!TryDimensions(document.Width, document.Height, dpi, out int width, out int height, out long pixels))
            return Fallback(representative, GradientFallbackKind.PixelBudget, "画像寸法の算術オーバーフロー");
        long available = request.AvailablePixels;
        if (pixels > available)
        {
            dpi *= Math.Sqrt(available / (double)pixels);
            reduction = $"画素予算 {available}px";
        }
        double dimensionScale = Math.Min(1.0, Math.Min(
            request.MaxDimensionPx / Math.Max(1.0, document.Width * dpi / Units.MmPerInch),
            request.MaxDimensionPx / Math.Max(1.0, document.Height * dpi / Units.MmPerInch)));
        if (dimensionScale < 1)
        {
            dpi *= dimensionScale;
            reduction = reduction == null ? $"1辺上限 {request.MaxDimensionPx}px"
                                          : reduction + $"・1辺上限 {request.MaxDimensionPx}px";
        }
        if (dpi < request.MinimumDpi)
            return Fallback(representative, GradientFallbackKind.PixelBudget,
                $"必要DPI {dpi:0.###} が最低 {request.MinimumDpi:0.###} 未満");
        if (!TryDimensions(document.Width, document.Height, dpi, out width, out height, out pixels)
            || pixels > available || width > request.MaxDimensionPx || height > request.MaxDimensionPx)
            return Fallback(representative, GradientFallbackKind.PixelBudget,
                "DPI低減後も画素予算を満たさない");

        try
        {
            byte[] png = Render(request, stops, local, document, localFromDocument, width, height);
            return new GradientHatchResult
            {
                Status = GradientHatchStatus.Rasterized,
                PngBytes = png,
                XMm = document.MinX,
                YMm = document.MinY,
                WidthMm = document.Width,
                HeightMm = document.Height,
                ActualDpi = dpi,
                WidthPx = width,
                HeightPx = height,
                PixelCount = pixels,
                SolidColor = representative,
                DpiReductionReason = reduction,
            };
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OverflowException)
        {
            return Fallback(representative, GradientFallbackKind.InvalidDefinition,
                $"PNG生成失敗: {ex.GetType().Name}");
        }
    }

    static byte[] Render(GradientHatchRequest request, GradientStop[] stops, Bounds local,
                         Bounds document, IrMatrix localFromDocument, int width, int height)
    {
        using var mask = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(mask))
        using (GraphicsPath path = BuildPath(request.Boundary, request.DocumentFromLocal, document, width, height))
        using (var brush = new SolidBrush(Color.White))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.FillPath(brush, path);
        }

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        BitmapData maskData = mask.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData outData = bitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int py = 0; py < height; py++)
                {
                    byte* mp = (byte*)maskData.Scan0 + py * maskData.Stride;
                    byte* op = (byte*)outData.Scan0 + py * outData.Stride;
                    double documentY = document.MinY + (py + .5) * document.Height / height;
                    for (int px = 0; px < width; px++, mp += 4, op += 4)
                    {
                        double documentX = document.MinX + (px + .5) * document.Width / width;
                        var lp = localFromDocument.Apply(documentX, documentY);
                        double t = GradientSemantics.Evaluate(request.GradientName, lp.X, lp.Y,
                            local.MinX, local.MinY, local.MaxX, local.MaxY,
                            request.AngleRadians, request.Shift);
                        IrRgb rgb = Interpolate(stops, Numbers.IsFinite(t) ? t : .5);
                        op[0] = IrRgb.ToByte(rgb.B);
                        op[1] = IrRgb.ToByte(rgb.G);
                        op[2] = IrRgb.ToByte(rgb.R);
                        op[3] = IrRgb.ToByte((mp[3] / 255.0) * request.Opacity);
                    }
                }
            }
        }
        finally
        {
            mask.UnlockBits(maskData);
            bitmap.UnlockBits(outData);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    static GraphicsPath BuildPath(IReadOnlyList<IrSegment> segments, IrMatrix transform,
                                  Bounds document, int width, int height)
    {
        var path = new GraphicsPath(FillMode.Alternate);
        PointF current = default;
        bool moved = false;
        PointF Map(double x, double y)
        {
            var p = transform.Apply(x, y);
            return new PointF((float)((p.X - document.MinX) * width / document.Width),
                              (float)((p.Y - document.MinY) * height / document.Height));
        }
        foreach (IrSegment segment in segments)
        {
            switch (segment)
            {
                case IrMove m:
                    path.StartFigure(); current = Map(m.X, m.Y); moved = true; break;
                case IrLine l when moved:
                    PointF end = Map(l.X, l.Y); path.AddLine(current, end); current = end; break;
                case IrCubic c when moved:
                    PointF c1 = Map(c.X1, c.Y1), c2 = Map(c.X2, c.Y2), e = Map(c.X, c.Y);
                    path.AddBezier(current, c1, c2, e); current = e; break;
                case IrClose when moved:
                    path.CloseFigure(); moved = false; break;
            }
        }
        return path;
    }

    static GradientStop[] ValidateStops(IReadOnlyList<GradientStop> source)
    {
        if (source == null || source.Count < 2) throw new ArgumentException("色ストップは2件以上必要");
        var indexed = source.Select((stop, index) => (stop, index)).ToArray();
        foreach (var item in indexed)
        {
            GradientStop s = item.stop;
            if (!Numbers.IsFinite(s.Position) || s.Position < 0 || s.Position > 1
                || !Numbers.IsFinite(s.Color.R) || !Numbers.IsFinite(s.Color.G) || !Numbers.IsFinite(s.Color.B))
                throw new ArgumentException("色ストップに非有限値または範囲外値がある");
        }
        return indexed.OrderBy(x => x.stop.Position).ThenBy(x => x.index).Select(x => x.stop).ToArray();
    }

    static IrRgb Interpolate(GradientStop[] stops, double t)
    {
        t = Math.Max(0, Math.Min(1, t));
        if (t <= stops[0].Position) return Clamp(stops[0].Color);
        for (int i = 1; i < stops.Length; i++)
        {
            if (t > stops[i].Position) continue;
            GradientStop a = stops[i - 1], b = stops[i];
            double span = b.Position - a.Position;
            double f = span <= 0 ? 1 : (t - a.Position) / span;
            return Clamp(new IrRgb(a.Color.R + (b.Color.R - a.Color.R) * f,
                                   a.Color.G + (b.Color.G - a.Color.G) * f,
                                   a.Color.B + (b.Color.B - a.Color.B) * f));
        }
        return Clamp(stops[stops.Length - 1].Color);
    }

    static bool TryBounds(IReadOnlyList<IrSegment> segments, IrMatrix transform, out Bounds bounds)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool valid = true;
        void Add(double x, double y)
        {
            var p = transform.Apply(x, y);
            if (!Numbers.IsFinite(p.X) || !Numbers.IsFinite(p.Y)) { valid = false; return; }
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y);
            maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        foreach (IrSegment segment in segments)
        {
            switch (segment)
            {
                case IrMove m: Add(m.X, m.Y); break;
                case IrLine l: Add(l.X, l.Y); break;
                case IrCubic c: Add(c.X1, c.Y1); Add(c.X2, c.Y2); Add(c.X, c.Y); break;
            }
        }
        bounds = new Bounds(minX, minY, maxX, maxY);
        return valid && minX <= maxX && minY <= maxY && bounds.Width > 0 && bounds.Height > 0;
    }

    static bool TryDimensions(double widthMm, double heightMm, double dpi,
                              out int width, out int height, out long pixels)
    {
        width = height = 0; pixels = 0;
        double wd = Math.Ceiling(widthMm * dpi / Units.MmPerInch), hd = Math.Ceiling(heightMm * dpi / Units.MmPerInch);
        if (!Numbers.IsFinite(wd) || !Numbers.IsFinite(hd) || wd < 1 || hd < 1 || wd > int.MaxValue || hd > int.MaxValue)
            return false;
        width = (int)wd; height = (int)hd;
        try { pixels = checked((long)width * height); }
        catch (OverflowException) { return false; }
        return true;
    }

    static GradientHatchResult Invalid(string reason) => new()
        { Status = GradientHatchStatus.SkippedInvalid,
          FallbackKind = GradientFallbackKind.InvalidDefinition, FallbackReason = reason };
    static GradientHatchResult Fallback(IrRgb color, GradientFallbackKind kind, string reason) => new()
        { Status = GradientHatchStatus.SolidFallback, SolidColor = color,
          FallbackKind = kind, FallbackReason = reason };
    static IrRgb Clamp(IrRgb c) => new(Math.Max(0, Math.Min(1, c.R)),
                                       Math.Max(0, Math.Min(1, c.G)),
                                       Math.Max(0, Math.Min(1, c.B)));
    readonly record struct Bounds(double MinX, double MinY, double MaxX, double MaxY)
    {
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }
}
