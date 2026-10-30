// 用紙の寸法対応付けの単体テスト。
//
// 候補の値は AutoCAD 2026 の "AutoCAD PDF (High Quality Print).pc3" が実際に返した公称寸法
// （tests/tools/regression/window_contract の計測器 PROBEMEDIA の出力）を使う。
// レイアウト側の 297.010651×419.988983 も実物図面（A3・DocuWorks Printer）の実測値である。
using System;
using System.Linq;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class PaperMediaMatcherTests
{
    /// <summary>発行デバイスが実際に持つ用紙（実測値の抜粋）。</summary>
    static readonly PaperMediaCandidate[] PdfDeviceMedia =
    {
        new("ANSI_A_(11.00_x_8.50_Inches)", 279.4, 215.9, 269.4, 181.9),
        new("ANSI_A_(8.50_x_11.00_Inches)", 215.9, 279.4, 205.9, 245.4),
        new("ANSI_B_(11.00_x_17.00_Inches)", 279.4, 431.8, 269.4, 397.8),
        new("ISO_A3_(420.00_x_297.00_MM)", 420.0, 297.0, 410.0, 263.0),
        new("ISO_full_bleed_A3_(420.00_x_297.00_MM)", 420.0, 297.0, 420.0, 297.0),
        new("ISO_A3_(297.00_x_420.00_MM)", 297.0, 420.0, 287.0, 386.0),
        new("ISO_full_bleed_A3_(297.00_x_420.00_MM)", 297.0, 420.0, 297.0, 420.0),
        new("ISO_A4_(210.00_x_297.00_MM)", 210.0, 297.0, 200.0, 263.0),
    };

    [Fact]
    public void 実物図面のA3は_発行デバイスの同寸法の用紙へ対応付く()
    {
        // 実物図面のレイアウト（用紙名 "A3"、DocuWorks Printer 固有の名前）は発行デバイスに無い。
        // 名前で引くだけだとレターへ落ち、1:1 の尺度が壊れていた（実測）
        string? m = PaperMediaMatcher.SelectByPaperSize(297.010651, 419.988983, PdfDeviceMedia);
        Assert.Equal("ISO_full_bleed_A3_(297.00_x_420.00_MM)", m);
    }

    [Fact]
    public void 向きが一致する用紙を_入れ替わりより優先する()
    {
        // 420×297（横）には横向きの候補があるため、縦向きの同一用紙を選ばない
        string? m = PaperMediaMatcher.SelectByPaperSize(420.0, 297.0, PdfDeviceMedia);
        Assert.Equal("ISO_full_bleed_A3_(420.00_x_297.00_MM)", m);
    }

    [Fact]
    public void 向きが一致する候補が無ければ_縦横入れ替わりの同一用紙を採る()
    {
        var onlyLandscape = new[] { new PaperMediaCandidate("ISO_A3_(420.00_x_297.00_MM)", 420.0, 297.0, 410.0, 263.0) };
        string? m = PaperMediaMatcher.SelectByPaperSize(297.0, 420.0, onlyLandscape);
        Assert.Equal("ISO_A3_(420.00_x_297.00_MM)", m);
    }

    [Fact]
    public void 同寸法の候補が複数あれば_印刷可能領域が広い方を採る()
    {
        // 成果物は窓へ切り抜くため用紙の余白は残らない。印刷可能領域が広いほど、
        // 要求どおりの尺度で窓を載せられ、用紙合わせへの強制切替を避けられる
        string? m = PaperMediaMatcher.SelectByPaperSize(420.0, 297.0, PdfDeviceMedia);
        Assert.Equal("ISO_full_bleed_A3_(420.00_x_297.00_MM)", m);
    }

    [Fact]
    public void 寸法の合う用紙が無ければ_黙って別の用紙を選ばない()
    {
        // B4（257×364）は候補に無い。近いからと A3 を返すと出力寸法が黙って変わる
        Assert.Null(PaperMediaMatcher.SelectByPaperSize(257.0, 364.0, PdfDeviceMedia));
    }

    [Theory]
    [InlineData(297.9, 420.9)]   // 許容差 1mm ちょうど手前
    [InlineData(296.1, 419.1)]
    public void 図面側の端数は_同じ用紙とみなす(double w, double h)
    {
        Assert.Equal("ISO_full_bleed_A3_(297.00_x_420.00_MM)", PaperMediaMatcher.SelectByPaperSize(w, h, PdfDeviceMedia));
    }

    [Theory]
    [InlineData(0, 420)]
    [InlineData(297, 0)]
    [InlineData(-297, 420)]
    public void 用紙寸法が取れていない図面は_対応付けの手がかりが無い(double w, double h)
    {
        Assert.Null(PaperMediaMatcher.SelectByPaperSize(w, h, PdfDeviceMedia));
    }

    [Fact]
    public void 候補が空でも_例外にせず対応なしを返す()
    {
        Assert.Null(PaperMediaMatcher.SelectByPaperSize(297.0, 420.0, System.Array.Empty<PaperMediaCandidate>()));
    }

    [Fact]
    public void 同名でも寸法が違えば採用せず同寸法の別名を選ぶ()
    {
        var candidates = new[]
        {
            new PaperMediaCandidate("A3", 279.4, 215.9, 270, 200),
            new PaperMediaCandidate("PDF_A3", 420, 297, 420, 297),
        };
        Assert.Equal(new PaperMediaResolution("PDF_A3", sizeVerified: true), PaperMediaMatcher.Resolve("A3", 420, 297, candidates));
    }

    [Fact]
    public void 同名かつ同寸法ならその候補を優先する()
    {
        var candidates = new[]
        {
            new PaperMediaCandidate("A3", 420, 297, 410, 287),
            new PaperMediaCandidate("別名", 420, 297, 420, 297),
        };
        Assert.Equal(new PaperMediaResolution("A3", sizeVerified: true), PaperMediaMatcher.Resolve("A3", 420, 297, candidates));
    }

    // ARES 2020 の .NET の API は、出力先を PDF にした後の用紙の一覧を 0 件で返す（COM では同じ出力先に 101 件ある。
    // 2026-10-25 実測）。一覧が無いと寸法で選べないので、ページ設定の用紙名をそのまま試す（使えるかは出力先が判定する）。
    // 寸法は確かめていないので、そのことを返す（呼び出し側が診断に書く）
    [Fact]
    public void 用紙の一覧が空なら_ページ設定の用紙名を寸法を確かめずに試す()
    {
        Assert.Equal(new PaperMediaResolution("ISO_full_bleed_A3_(420.00_x_297.00_MM)", sizeVerified: false),
            PaperMediaMatcher.Resolve("ISO_full_bleed_A3_(420.00_x_297.00_MM)", 420, 297, Array.Empty<PaperMediaCandidate>()));
    }

    // 寸法で対応付けられなかった理由は、用紙の一覧の状況に合わせる（一覧が空、どの用紙も寸法を取れない、
    // 一部だけ寸法を取れた、すべて寸法を取れた）。寸法を確かめずに用紙名を使ったときの診断にも、この理由を書く
    [Theory]
    [InlineData(0, 0, "plotport.media_list_empty")]
    [InlineData(101, 0, "plotport.media_sizes_unavailable")]
    [InlineData(101, 99, "plotport.media_not_found")]
    [InlineData(101, 101, "plotport.media_not_found_all")]
    public void 寸法で対応付けられなかった理由は用紙の一覧の状況に合わせる(int nameCount, int measuredCount, string expectedKey)
    {
        Assert.Equal(expectedKey, PaperMediaMatcher.MissingReasonKey(nameCount, measuredCount));
    }

    [Fact]
    public void 用紙の一覧もページ設定の用紙名も無ければ_用紙を選ばない()
    {
        Assert.Null(PaperMediaMatcher.Resolve("", 420, 297, Array.Empty<PaperMediaCandidate>()).Name);
    }

    [Fact]
    public void 既定A4がなければA4を収容する最小用紙を候補順に依存せず選ぶ()
    {
        var candidates = new[]
        {
            new PaperMediaCandidate("A1", 841, 594, 820, 570),
            new PaperMediaCandidate("A3", 420, 297, 410, 287),
            new PaperMediaCandidate("A2", 594, 420, 580, 400),
        };
        Assert.Equal("A3", PaperMediaMatcher.SelectFallback(210, 297, candidates));
        Assert.Equal("A3", PaperMediaMatcher.SelectFallback(210, 297, candidates.AsEnumerable().Reverse()));
    }

    [Fact]
    public void 全候補より大きい独自用紙は最大用紙へ縮退する()
    {
        Assert.Equal("A3", PaperMediaMatcher.SelectFallback(1000, 1000, new[]
        {
            new PaperMediaCandidate("A4", 297, 210, 287, 200),
            new PaperMediaCandidate("A3", 420, 297, 410, 287),
        }));
    }

    [Fact]
    public void A4横用紙しかなくても回転可能な同寸法をA3より優先する()
    {
        Assert.Equal("A4横", PaperMediaMatcher.SelectFallback(210, 297, new[]
        {
            new PaperMediaCandidate("A3", 297, 420, 287, 410),
            new PaperMediaCandidate("A4横", 297, 210, 287, 200),
        }));
    }

    [Fact]
    public void 許容差内の非標準小用紙より公称寸法一致を優先する()
    {
        Assert.Equal("A4", PaperMediaMatcher.SelectFallback(210, 297, new[]
        {
            new PaperMediaCandidate("小さい近似", 209.5, 296.5, 199.5, 286.5),
            new PaperMediaCandidate("A4", 210, 297, 200, 287),
        }));
    }
}
