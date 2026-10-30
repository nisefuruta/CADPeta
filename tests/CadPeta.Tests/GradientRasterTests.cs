using System;
using System.IO;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using CadPeta.Acquire.Raster;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class GradientRasterTests
{
    // 再生成: tests/tools/pdf-emf/gradient_fit の pnghash モード。
    // 同じ入力をnet48/net8/net10で実行し、GDI+ PNG符号化の版差を検知する。
    const string RasterPngCrossTfmGoldenSha256 =
        "B02AE8570093C405618F7A64067203D63EBC4AEBAEBCC0D8AAB95D7CD3D52E2A";

    static readonly string[] Names =
    {
        "LINEAR", "CYLINDER", "INVCYLINDER", "SPHERICAL", "INVSPHERICAL",
        "HEMISPHERICAL", "INVHEMISPHERICAL", "CURVED", "INVCURVED",
    };

    [Fact]
    public void 公式9形状は有限値を返し逆形は補数になる()
    {
        foreach (string name in Names)
        {
            double value = GradientSemantics.Evaluate(name, 37, 29, 0, 0, 100, 80, Math.PI / 4, 0);
            Assert.InRange(value, 0, 1);
        }
        foreach ((string normal, string inverse) in new[]
        {
            ("CYLINDER", "INVCYLINDER"), ("SPHERICAL", "INVSPHERICAL"),
            ("HEMISPHERICAL", "INVHEMISPHERICAL"), ("CURVED", "INVCURVED"),
        })
        {
            double a = GradientSemantics.Evaluate(normal, 37, 29, 0, 0, 100, 80, .3, 0);
            double b = GradientSemantics.Evaluate(inverse, 37, 29, 0, 0, 100, 80, .3, 0);
            Assert.Equal(1, a + b, 12);
        }
    }

    [Fact]
    public void AutoCAD実測27チャートと空間式が固定許容差内で一致する()
    {
        using Stream stream = typeof(GradientRasterTests).Assembly.GetManifestResourceStream(
            "CadPeta.Tests.gradient_golden_samples.json")!;
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        double width = root.GetProperty("HatchWidthMm").GetDouble();
        double height = root.GetProperty("HatchHeightMm").GetDouble();
        double edge = root.GetProperty("ExcludedEdgeMm").GetDouble();
        int columns = root.GetProperty("SampleColumns").GetInt32();
        int rows = root.GetProperty("SampleRows").GetInt32();
        double medianLimit = root.GetProperty("MedianErrorLimitLevels").GetDouble();
        double p95Limit = root.GetProperty("P95ErrorLimitLevels").GetDouble();
        double rmseLimit = root.GetProperty("RmseLimitLevels").GetDouble();
        string repositoryRoot = RepositoryPaths.Root();

        foreach (JsonElement chart in root.GetProperty("Charts").EnumerateArray())
        {
            string shape = chart.GetProperty("Shape").GetString()!;
            string suffix = chart.GetProperty("Suffix").GetString()!;
            double angle = chart.GetProperty("AngleRadians").GetDouble();
            double shift = chart.GetProperty("Shift").GetDouble();
            byte[] measured = chart.GetProperty("Values").GetBytesFromBase64();
            Assert.Equal(columns * rows, measured.Length);

            var errors = new double[measured.Length];
            double squareSum = 0;
            for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                double x = edge + column * (width - 2 * edge) / (columns - 1);
                double y = edge + row * (height - 2 * edge) / (rows - 1);
                double predicted = GradientSemantics.Evaluate(shape, x, y, 0, 0, width, height, angle, shift) * 255;
                double error = Math.Abs(measured[row * columns + column] - predicted);
                errors[row * columns + column] = error;
                squareSum += error * error;
            }
            Array.Sort(errors);
            double median = Percentile(errors, .5);
            double p95 = Percentile(errors, .95);
            double rmse = Math.Sqrt(squareSum / errors.Length);
            string label = $"{shape} {suffix}: median={median:0.00}, p95={p95:0.00}, rmse={rmse:0.00}";
            Assert.True(median <= medianLimit, label);
            Assert.True(p95 <= p95Limit, label);
            Assert.True(rmse <= rmseLimit, label);

            string pdf = Path.Combine(repositoryRoot, "tests", "assets", "testcharts", "gradient", "charts",
                chart.GetProperty("SourcePdf").GetString()!);
            Assert.Equal(chart.GetProperty("SourcePdfSha256").GetString(), Sha256(pdf));
        }
    }

    [Fact]
    public void LINEARの基準軸と45度を実測式どおり評価する()
    {
        Assert.Equal(.25, GradientSemantics.Evaluate("LINEAR", 25, 40, 0, 0, 100, 80, 0, 0), 12);
        // IR Y下向き。45度では左下→右上で、射影spanは 100/sqrt(2)+80/sqrt(2)。
        double expected = .5 + (25 / Math.Sqrt(2) - (-20) / Math.Sqrt(2)) /
                                 (100 / Math.Sqrt(2) + 80 / Math.Sqrt(2));
        Assert.Equal(expected, GradientSemantics.Evaluate("LINEAR", 75, 20, 0, 0, 100, 80, Math.PI / 4, 0), 12);
    }

    [Fact]
    public void ラスタ化は穴をstraightAlphaへ焼き込み寸法と予算を返す()
    {
        var request = GradientTestRequests.Basic() with
        {
            Boundary = new IrSegment[]
            {
                new IrMove(0, 0), new IrLine(25.4, 0), new IrLine(25.4, 25.4), new IrLine(0, 25.4), new IrClose(),
                new IrMove(8, 8), new IrLine(8, 17.4), new IrLine(17.4, 17.4), new IrLine(17.4, 8), new IrClose(),
            },
            Opacity = .5,
        };
        GradientHatchResult result = GradientRasterizer.Rasterize(request);
        Assert.Equal(GradientHatchStatus.Rasterized, result.Status);
        Assert.Equal(GradientFallbackKind.None, result.FallbackKind);
        Assert.Equal(10, result.WidthPx);
        Assert.Equal(10, result.HeightPx);
        Assert.Equal(100, result.PixelCount);
        using var stream = new MemoryStream(result.PngBytes);
        using var bitmap = new Bitmap(stream);
        Assert.InRange(bitmap.GetPixel(1, 1).A, 126, 128);
        Assert.Equal(0, bitmap.GetPixel(5, 5).A);
    }

    [Fact]
    public void 同一入力のPNGは全TFM共通のバイト列になる()
    {
        GradientHatchResult result = GradientRasterizer.Rasterize(GradientTestRequests.Basic());
        Assert.Equal(GradientHatchStatus.Rasterized, result.Status);
        Assert.Equal(RasterPngCrossTfmGoldenSha256, Sha256(result.PngBytes));
    }

    [Fact]
    public void 画素上限はDPIを決定的に下げ最低DPI未満なら代表色へ縮退する()
    {
        GradientHatchResult reduced = GradientRasterizer.Rasterize(GradientTestRequests.Basic() with
        {
            RequestedDpi = 100, AvailablePixels = 100, MinimumDpi = 1,
        });
        Assert.Equal(GradientHatchStatus.Rasterized, reduced.Status);
        Assert.True(reduced.ActualDpi < 100);
        Assert.NotNull(reduced.DpiReductionReason);

        GradientHatchResult fallback = GradientRasterizer.Rasterize(GradientTestRequests.Basic() with
        {
            RequestedDpi = 100, AvailablePixels = 4, MinimumDpi = 10,
        });
        Assert.Equal(GradientHatchStatus.SolidFallback, fallback.Status);
        Assert.Equal(GradientFallbackKind.PixelBudget, fallback.FallbackKind);
        Assert.Contains("最低", fallback.FallbackReason);
    }

    [Fact]
    public void 不正定義と特異行列は安全に終了する()
    {
        Assert.Equal(GradientHatchStatus.SkippedInvalid,
            GradientRasterizer.Rasterize(GradientTestRequests.Basic() with { Stops = Array.Empty<GradientStop>() }).Status);
        Assert.Equal(GradientFallbackKind.UnknownShape,
            GradientRasterizer.Rasterize(GradientTestRequests.Basic() with { GradientName = "UNKNOWN" }).FallbackKind);
        Assert.Equal(GradientHatchStatus.SolidFallback,
            GradientRasterizer.Rasterize(GradientTestRequests.Basic() with
            { DocumentFromLocal = new IrMatrix(1, 0, 2, 0, 0, 0) }).Status);
        Assert.Equal(GradientFallbackKind.InvalidDefinition,
            GradientRasterizer.Rasterize(GradientTestRequests.Basic() with
            { DocumentFromLocal = new IrMatrix(1, 0, 2, 0, 0, 0) }).FallbackKind);
        Assert.Equal(GradientHatchStatus.SkippedInvalid,
            GradientRasterizer.Rasterize(GradientTestRequests.Basic() with
            {
                Boundary = new IrSegment[]
                {
                    new IrMove(0, 0), new IrLine(double.NaN, 0),
                    new IrLine(25.4, 25.4), new IrLine(0, 25.4), new IrClose(),
                },
            }).Status);
    }

    [Fact]
    public void 取得予算は成功分だけを消費する()
    {
        var budget = new GradientRasterBudget(10);
        Assert.True(budget.TryConsume(6));
        Assert.False(budget.TryConsume(5));
        Assert.Equal(4, budget.RemainingPixels);
    }

    static double Percentile(double[] sorted, double fraction) =>
        sorted[(int)Math.Min(sorted.Length - 1, Math.Round((sorted.Length - 1) * fraction))];

    static string Sha256(string path)
    {
        using var algorithm = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(algorithm.ComputeHash(stream)).Replace("-", "");
    }

    static string Sha256(byte[] bytes)
    {
        using var algorithm = SHA256.Create();
        return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-", "");
    }
}
