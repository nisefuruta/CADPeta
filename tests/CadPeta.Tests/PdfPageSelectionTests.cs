// PDF のページを選んで取り込む約束の試験。pages_fixture.pdf はページごとに用紙の大きさが違う
// （tests/tools/pdf-emf/gen_page_color_fixtures.py: 1 = 100×80mm、2 = 120×90mm、3 = 150×100mm）。
using System;
using CadPeta.Acquire.Pdf;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public sealed class PdfPageSelectionTests
{
    static readonly string PagesPdf = RepositoryPaths.PdfFixture("pages_fixture.pdf");

    [Theory]
    [InlineData(1, 100, 80)]
    [InlineData(2, 120, 90)]
    [InlineData(3, 150, 100)]
    public void 指定したページを取り込む(int page, double widthMm, double heightMm)
    {
        var ir = new PdfIrAcquirer().Acquire(PagesPdf, new IrDiagnostics(), page);

        Assert.Equal((widthMm, heightMm), (Math.Round(ir.WidthMm, 1), Math.Round(ir.HeightMm, 1)));
    }

    [Fact]
    public void ページを指定しなければ1ページ目を取り込む()
    {
        var ir = new PdfIrAcquirer().Acquire(PagesPdf, new IrDiagnostics());

        Assert.Equal(100, Math.Round(ir.WidthMm, 1));
    }

    [Fact]
    public void ページ数を返す()
    {
        Assert.Equal(3, PdfIrAcquirer.CountPages(PagesPdf));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void PDFに無いページは理由とページ数を付けて失敗する(int page)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new PdfIrAcquirer().Acquire(PagesPdf, new IrDiagnostics(), page));

        Assert.Contains("3 ページ", error.Message);
    }
}
