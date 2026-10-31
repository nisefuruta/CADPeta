using System;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Tests;
using CadPeta.Transforms;
using CadPeta.UseCases;
using Xunit;
using static CadPeta.App.Tests.TestRequests;

namespace CadPeta.App.Tests;

public sealed class PdfPetaValidationTests
{
    [Fact]
    public void Valid_request_has_no_invariant_errors()
    {
        using var temp = new TempFolder();
        Assert.Empty(Request(new PdfPetaOutput(ArtifactKind.Png, temp.File("a.png"), 150)).ValidateInvariants());
    }

    [Fact]
    public void Pipeline共通契約が非有限cropと重複pathを拒否する()
    {
        using var temp = new TempFolder();
        string path = temp.File("same.png");
        var request = Request(new(ArtifactKind.Png, path, 150), new(ArtifactKind.Svg, path)) with
        { CropRectMm = new RectMm(0, 0, double.NaN, 10) };
        var commonErrors = request.ValidateInvariants();
        Assert.Contains(commonErrors, x => x.Contains("クロップ"));
        Assert.Contains(commonErrors, x => x.Contains("重複"));
        var guiErrors = PdfPetaGuiPresenter.ValidateForGui(request);
        Assert.Contains(guiErrors, x => x.Contains("クロップ"));
        Assert.Contains(guiErrors, x => x.Contains("重複"));
    }

    [Fact]
    public void 入力上書き_拡張子違い_同種複数を共通契約で拒否する()
    {
        using var temp = new TempFolder();
        string input = temp.File("input.pdf");
        var request = new PdfPetaRequest
        {
            InputPdfPath = input,
            Outputs = new[]
            {
                new PdfPetaOutput(ArtifactKind.Pdf, input),
                new PdfPetaOutput(ArtifactKind.Svg, temp.File("wrong.pdf")),
                new PdfPetaOutput(ArtifactKind.Svg, temp.File("second.svg")),
            },
        };
        var errors = request.ValidateInvariants();
        Assert.Contains(errors, x => x.Contains("同じパス"));
        Assert.Contains(errors, x => x.Contains("拡張子"));
        Assert.Contains(errors, x => x.Contains("最大1件"));
    }

    // PNG の解像度の範囲は PETA の設定と同じ（CadPetaSettings の 1 か所で決める）
    [Theory]
    [InlineData(RasterLimits.MinDpi - 1)]
    [InlineData(CadPetaSettings.MaxPngDpi + 1)]
    public void PNGの解像度は設定と同じ範囲の外を拒否する(double dpi)
    {
        using var temp = new TempFolder();
        var errors = Request(new PdfPetaOutput(ArtifactKind.Png, temp.File("a.png"), dpi)).ValidateInvariants();
        Assert.Contains(CadPetaSettings.ValidatePngDpi(dpi)!, errors);
    }

    [Fact]
    public void クリップボードに登録できるのはEMFだけ()
    {
        using var temp = new TempFolder();
        var errors = Request(new PdfPetaOutput(ArtifactKind.Svg, temp.File("a.svg"), ToClipboard: true)).ValidateInvariants();
        Assert.Contains(errors, x => x.Contains("クリップボード"));
    }

    [Fact]
    public void ページの番号は1以上()
    {
        using var temp = new TempFolder();
        var errors = (Request(new PdfPetaOutput(ArtifactKind.Svg, temp.File("a.svg"))) with { PageNumber = 0 }).ValidateInvariants();
        Assert.Contains(errors, x => x.Contains("ページ"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(ColorReplacement.MaxTolerance + 1)]
    public void 色置換の許容差は0から255の外を拒否する(int tolerance)
    {
        using var temp = new TempFolder();
        var errors = (Request(new PdfPetaOutput(ArtifactKind.Svg, temp.File("a.svg"))) with
        { ColorReplacements = new[] { new ColorReplacement(new IrRgb(1, 0, 0), new IrRgb(0, 0, 0), tolerance) } }).ValidateInvariants();
        Assert.Contains(errors, x => x.Contains("許容差"));
    }

    [Theory]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#FF8040", 1, 128.0 / 255, 64.0 / 255)]
    public void Parses_hex_rgb(string text, double r, double g, double b)
    { var value = IrRgb.ParseHex(text); Assert.Equal(r, value.R, 10); Assert.Equal(g, value.G, 10); Assert.Equal(b, value.B, 10); }

    [Theory] [InlineData("")] [InlineData("FF0000")] [InlineData("#GG0000")]
    public void Rejects_invalid_hex_rgb(string text) => Assert.False(IrRgb.TryParseHex(text, out _));

    [Fact]
    public void App_does_not_reference_CAD_SDK_assemblies()
    {
        var names = typeof(PdfPetaPipeline).Assembly.GetReferencedAssemblies().Select(x => x.Name).ToArray();
        Assert.DoesNotContain(names, x => x is not null && (x.Contains("AcMgd", StringComparison.OrdinalIgnoreCase)
            || x.Contains("BrxMgd", StringComparison.OrdinalIgnoreCase) || x.Contains("GcMgd", StringComparison.OrdinalIgnoreCase)
            || x.Contains("TD_Mgd", StringComparison.OrdinalIgnoreCase)));
    }

}
