using System;
using System.IO;
using System.Threading;
using CadPeta.Tests;
using Xunit;

namespace CadPeta.App.Tests;

public sealed class PdfPetaPipelineTests
{
    [Fact]
    public void Converts_pdf_to_svg_and_reports_diagnostics()
    {
        string input = SampleChartPdf;
        using var temp = new TempFolder();
        string output = temp.File("result.svg"); int boundaries = 0;
        var result = PdfPetaPipeline.Run(new PdfPetaRequest { InputPdfPath = input,
            Outputs = new[] { new PdfPetaOutput(PdfPetaOutputKind.Svg, output) } }, TextWriter.Null,
            new PdfPetaRunOptions { DiagnosticStageSink = (_, _, _) => boundaries++ });
        Assert.True(new FileInfo(output).Length > 0); Assert.True(boundaries >= 2); Assert.NotNull(result.Diagnostics);
    }

    [Fact]
    public void Honors_cancellation_before_acquisition()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfPetaPipeline.Run(new PdfPetaRequest
        { InputPdfPath = "missing.pdf", Outputs = new[] { new PdfPetaOutput(PdfPetaOutputKind.Svg, "out.svg") } },
        TextWriter.Null, cancellationToken: cts.Token));
    }

    [Fact]
    public void Clipboard_log_is_after_all_file_outputs_for_cli_compatibility()
    {
        string input = SampleChartPdf;
        using var temp = new TempFolder();
        using var log = new StringWriter();
        var request = new PdfPetaRequest { InputPdfPath = input, Outputs = new[]
        {
            new PdfPetaOutput(PdfPetaOutputKind.Clipboard, temp.File("clipboard.emf")),
            new PdfPetaOutput(PdfPetaOutputKind.Png, temp.File("result.png"), 96),
        }};
        PdfPetaPipeline.Run(request, log, new PdfPetaRunOptions
        { ClipboardSetter = (_, writer) => writer.WriteLine("CLIPBOARD-LAST") });
        string output = log.ToString();
        Assert.True(output.IndexOf("PNG出力:", StringComparison.Ordinal) >= 0);
        Assert.True(output.IndexOf("PNG出力:", StringComparison.Ordinal)
            < output.IndexOf("CLIPBOARD-LAST", StringComparison.Ordinal));
    }

    static string SampleChartPdf => RepositoryPaths.Asset("testcharts", "PDF出力20260706-01.pdf");
}
