namespace CadPeta.App.Tests;

/// <summary>試験の変換の要求。入力の PDF の名前だけを決め、出力は試験ごとに渡す（入力のファイルは読まない検査に使う）。</summary>
internal static class TestRequests
{
    public static PdfPetaRequest Request(params PdfPetaOutput[] outputs) => new() { InputPdfPath = "input.pdf", Outputs = outputs };
}