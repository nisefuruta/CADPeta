// 出力アダプタ: IrDocument → PNG（GDI+ による直接ラスタライズ、FR-3）。
//
// 確定事項を実装する:
// - System.Drawing（GDI+）で IR を直接描画する。フォント描画は EmfWriter の文字写像の
//   意味論（グリフごとのX位置列 GlyphXsMm・幅係数・回転）を踏襲する
// - アンチエイリアス有効・白背景。既定 DPI は Chrome ヘッダレス比較検証の実績値（下記定数）
// - 座標系: Graphics.Transform に「mm→px の一括変換」を1回だけ設定し、以降のあらゆる描画
//   呼び出し（パス座標・グループ変換行列・フォントサイズ・線幅・破線長）へ IR の mm 値を
//   そのまま渡す（EMF の LX/LY 一括変換・PDF のページ先頭 cm と同じ考え方）。
//   IR・ビットマップともY軸下向きのため、PDFと異なりY反転は不要
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Out.Png;

public sealed class PngWriter : IIrOutput
{
    /// <summary>既定DPI。Chromeヘッダレスレンダとの画素比較検証で実績のある値。</summary>
    public const double DefaultDpi = 150.0;

    // 幅係数を掛けない（等倍とみなす）帯。GDI+ の Font/Pen は幅係数の専用パラメータを
    // 持たないため ScaleTransform で表現するが、丸め誤差程度の差で余計な変換を足さない
    const double WidthFactorTol = 0.001;
    // ダッシュ長→ペン幅倍数の変換でゼロ割を避けるための最小ペン幅（mm）。
    // IR 側で最細線は正の固定幅へ正規化済みのため通常は働かない安全網
    const double MinPenWidthMm = 0.001;

    readonly double _dpi;
    readonly TextWriter? _log;

    /// <param name="dpi">出力解像度（既定 <see cref="DefaultDpi"/>）。</param>
    /// <param name="log">情報メッセージの出力先（null で沈黙）。診断ログとは別の進行報告。</param>
    public PngWriter(double dpi = DefaultDpi, TextWriter? log = null)
    {
        if (dpi <= 0) throw new ArgumentOutOfRangeException(nameof(dpi), dpi, "DPIは正の値である必要がある");
        _dpi = dpi;
        _log = log;
    }

    public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
    {
        string path = Path.GetFullPath(outputPath);
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        double pxPerMm = _dpi / Units.MmPerInch;
        int widthPx = Math.Max(1, (int)Math.Round(ir.WidthMm * pxPerMm));
        int heightPx = Math.Max(1, (int)Math.Round(ir.HeightMm * pxPerMm));

        var clipDefs = ir.ClipDefs.ToDictionary(c => c.Id);
        var fontCache = new Dictionary<string, FontFamily>();
        int nPaths = 0, nTexts = 0, nTextsSkipped = 0, nImages = 0, nFontSubstituted = 0;

        using var bmp = new Bitmap(widthPx, heightPx, PixelFormat.Format32bppArgb);
        bmp.SetResolution((float)_dpi, (float)_dpi);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.White);
            // mm→px 変換とクロップ原点の平行移動を1回でまとめる（以降は生の mm 値を渡せばよい）
            g.Transform = new Matrix((float)pxPerMm, 0, 0, (float)pxPerMm,
                                     (float)(-ir.ViewMinXMm * pxPerMm), (float)(-ir.ViewMinYMm * pxPerMm));

            FontFamily ResolveFamily(string name)
            {
                if (fontCache.TryGetValue(name, out var f)) return f;
                try
                {
                    f = new FontFamily(name);
                }
                catch (ArgumentException)
                {
                    f = FontFamily.GenericSansSerif;
                    diag.Add("PNG出力: フォント代替（同名フォントが見つからない）",
                             $"font={name} → {f.Name}");
                    nFontSubstituted++;
                }
                fontCache[name] = f;
                return f;
            }

            void WithClip(string? clipId, Action body)
            {
                GraphicsState? saved = null;
                if (clipId != null)
                {
                    if (!clipDefs.TryGetValue(clipId, out var def))
                        throw new InvalidOperationException($"クリップ定義が見つからない: id={clipId}（IR不整合）");
                    saved = g.Save();
                    using var gp = BuildPath(def.Segments, def.EvenOdd);
                    g.SetClip(gp, CombineMode.Intersect);
                }
                body();
                if (saved is { } s) g.Restore(s);
            }

            static Color ToColor(IrRgb c, double opacity)
            {
                // 255 = 8bit色成分の最大値（0..1 の各成分・不透明度を 8bit へ量子化）
                const int Max8Bit = 255;
                int a = Math.Min(Max8Bit, Math.Max(0, (int)Math.Round(opacity * Max8Bit)));
                int r = Math.Min(Max8Bit, Math.Max(0, (int)Math.Round(c.R * Max8Bit)));
                int gg = Math.Min(Max8Bit, Math.Max(0, (int)Math.Round(c.G * Max8Bit)));
                int b = Math.Min(Max8Bit, Math.Max(0, (int)Math.Round(c.B * Max8Bit)));
                return Color.FromArgb(a, r, gg, b);
            }

            void WriteNode(IrNode node)
            {
                switch (node)
                {
                    case IrGroup grp:
                        WithClip(grp.ClipId, () =>
                        {
                            var saved = g.Save();
                            if (!grp.Transform.IsIdentity)
                            {
                                var m = grp.Transform;
                                g.MultiplyTransform(new Matrix((float)m.A, (float)m.B, (float)m.C,
                                                               (float)m.D, (float)m.E, (float)m.F));
                            }
                            foreach (var child in grp.Children)
                                WriteNode(child);
                            g.Restore(saved);
                        });
                        break;

                    case IrPath p:
                        if (p.Fill == null && p.Stroke == null)
                            break;
                        WithClip(p.ClipId, () =>
                        {
                            using var gp = BuildPath(p.Segments, p.FillEvenOdd);
                            if (p.Fill != null)
                            {
                                using var brush = new SolidBrush(ToColor(p.Fill.Value, p.FillOpacity));
                                g.FillPath(brush, gp);
                            }
                            if (p.Stroke != null)
                            {
                                double penWidthMm = Math.Max(MinPenWidthMm, p.StrokeWidthMm);
                                using var pen = new Pen(ToColor(p.Stroke.Value, p.StrokeOpacity), (float)penWidthMm);
                                if (p.StrokeDashMm is { Length: > 0 } dash)
                                {
                                    // Pen.DashPattern の単位はペン幅の倍数（絶対mmではない）。
                                    // dash[i]/penWidthMm と定義すると、実描画長は
                                    // (dash[i]/penWidthMm)*penWidthMm=dash[i] となり penWidthMm に依らず一致する
                                    pen.DashPattern = dash.Select(mm => (float)(mm / penWidthMm)).ToArray();
                                    if (p.StrokeDashOffsetMm != 0)
                                        pen.DashOffset = (float)(p.StrokeDashOffsetMm / penWidthMm);
                                }
                                g.DrawPath(pen, gp);
                            }
                        });
                        nPaths++;
                        break;

                    case IrText t:
                    {
                        // デバイス空間での実際の字高（px）が極小の文字はGDI+の文字描画
                        // （Graphics.DrawString）が内部エラー（GDI+の"Generic error"）を返す
                        // ことを実物図面で確認した（原寸0.0035mmの文字。原因は不明だがGDI+の
                        // ラスタライズ限界と見られる）。デバイス0.5px未満は肉眼でもAAでもほぼ
                        // 不可視（Chrome等のSVGレンダでも実質見えない大きさ）のため、描画を
                        // 省略して理由を記録する（クラッシュより実害が小さい安全側の判断）
                        const double MinVisibleDevicePx = 0.5;
                        double devicePx = t.FontSizeMm * pxPerMm;
                        if (devicePx < MinVisibleDevicePx)
                        {
                            diag.Add("PNG出力: 文字サイズが微小のため描画を省略",
                                     $"'{t.Value}' size={t.FontSizeMm:F6}mm ({devicePx:F3}px@{_dpi:F0}dpi)");
                            nTextsSkipped++;
                            break;
                        }
                        WithClip(t.ClipId, () =>
                        {
                            var family = ResolveFamily(t.FontFamily);
                            using var brush = new SolidBrush(ToColor(t.Fill ?? new IrRgb(0, 0, 0), t.FillOpacity));
                            float emSizePx = (float)t.FontSizeMm; // GraphicsUnit.Pixel=世界単位(mm相当)
                            using var font = new Font(family, emSizePx, FontStyle.Regular, GraphicsUnit.Pixel);
                            // ベースライン合わせ: DrawString の原点はレイアウト枠の左上のため、
                            // アセント分だけ上へ描画してベースラインを y=0 に合わせる
                            float ascentRatio = family.GetCellAscent(FontStyle.Regular)
                                              / (float)family.GetEmHeight(FontStyle.Regular);
                            float ascentMm = emSizePx * ascentRatio;

                            var saved = g.Save();
                            g.TranslateTransform((float)t.XMm, (float)t.YMm);
                            if (t.RotationDeg != 0.0)
                                g.RotateTransform((float)t.RotationDeg); // IRは時計回り正、GDI+も同じ向き（Y下向き空間）
                            if (Math.Abs(t.WidthFactor - 1.0) > WidthFactorTol)
                                g.ScaleTransform((float)t.WidthFactor, 1f);

                            int n = t.Value.Length;
                            if (t.GlyphXsMm is { Length: > 1 } xs && xs.Length == n)
                            {
                                for (int i = 0; i < n; i++)
                                {
                                    float lx = (float)((xs[i] - t.XMm) / t.WidthFactor);
                                    g.DrawString(t.Value[i].ToString(), font, brush, lx, -ascentMm,
                                                 StringFormat.GenericTypographic);
                                }
                            }
                            else
                            {
                                g.DrawString(t.Value, font, brush, 0f, -ascentMm, StringFormat.GenericTypographic);
                            }
                            g.Restore(saved);
                        });
                        nTexts++;
                        break;
                    }

                    case IrImage im:
                        WithClip(im.ClipId, () =>
                        {
                            using var bitmap = new Bitmap(new MemoryStream(im.PngBytes));
                            var dest = new RectangleF((float)im.XMm, (float)im.YMm,
                                                      (float)im.WidthMm, (float)im.HeightMm);
                            g.DrawImage(bitmap, dest); // GDI+ のアルファ合成をそのまま使う（無劣化）
                        });
                        nImages++;
                        break;

                    default:
                        throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
                }
            }

            foreach (var node in ir.Nodes)
                WriteNode(node);
        }
        foreach (var f in fontCache.Values)
            if (!ReferenceEquals(f, FontFamily.GenericSansSerif)) f.Dispose();

        bmp.Save(path, ImageFormat.Png);

        _log?.WriteLine($"PNG: paths={nPaths} texts={nTexts} 省略={nTextsSkipped} images={nImages} " +
                        $"font代替={nFontSubstituted} {widthPx}x{heightPx}px @{_dpi:F0}dpi " +
                        $"({new FileInfo(path).Length:N0} bytes)");
    }

    static GraphicsPath BuildPath(List<IrSegment> segs, bool evenOdd)
    {
        var gp = new GraphicsPath(evenOdd ? FillMode.Alternate : FillMode.Winding);
        double cx = 0, cy = 0, sx = 0, sy = 0;
        bool first = true;
        foreach (var s in segs)
            switch (s)
            {
                case IrMove m:
                    if (!first) gp.StartFigure();
                    first = false;
                    cx = sx = m.X; cy = sy = m.Y;
                    break;
                case IrLine l:
                    gp.AddLine((float)cx, (float)cy, (float)l.X, (float)l.Y);
                    cx = l.X; cy = l.Y;
                    break;
                case IrCubic c:
                    gp.AddBezier((float)cx, (float)cy, (float)c.X1, (float)c.Y1,
                                 (float)c.X2, (float)c.Y2, (float)c.X, (float)c.Y);
                    cx = c.X; cy = c.Y;
                    break;
                case IrClose:
                    gp.CloseFigure();
                    cx = sx; cy = sy;
                    break;
                default:
                    throw new InvalidOperationException($"未知のセグメント: {s}");
            }
        return gp;
    }
}
