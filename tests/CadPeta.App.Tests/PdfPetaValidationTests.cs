using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.App.Tests;

public sealed class PdfPetaValidationTests
{
    [Fact] public void Valid_request_has_no_invariant_errors() =>
        Assert.Empty(Request(new PdfPetaOutput(PdfPetaOutputKind.Png, Path.Combine(Path.GetTempPath(), "a.png"), 150)).ValidateInvariants());

    [Fact]
    public void Pipeline共通契約が非有限cropと重複pathを拒否する()
    {
        string path = Path.Combine(Path.GetTempPath(), "same.png");
        var request = Request(new(PdfPetaOutputKind.Png, path, 150), new(PdfPetaOutputKind.Svg, path)) with
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
        string input = Path.Combine(Path.GetTempPath(), "input.pdf");
        var request = new PdfPetaRequest
        {
            InputPdfPath = input,
            Outputs = new[]
            {
                new PdfPetaOutput(PdfPetaOutputKind.Pdf, input),
                new PdfPetaOutput(PdfPetaOutputKind.Svg, Path.Combine(Path.GetTempPath(), "wrong.pdf")),
                new PdfPetaOutput(PdfPetaOutputKind.Svg, Path.Combine(Path.GetTempPath(), "second.svg")),
            },
        };
        var errors = request.ValidateInvariants();
        Assert.Contains(errors, x => x.Contains("同じパス"));
        Assert.Contains(errors, x => x.Contains("拡張子"));
        Assert.Contains(errors, x => x.Contains("最大1件"));
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

    static PdfPetaRequest Request(params PdfPetaOutput[] outputs) => new()
    { InputPdfPath = "input.pdf", Outputs = outputs, EmfBackground = new IrRgb(1, 1, 1) };
}
