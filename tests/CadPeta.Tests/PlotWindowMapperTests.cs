// 窓プロット配置計算（PlotWindowMapper）の単体テスト。
// 尺度（fit）と、中央配置矩形の用紙座標→表示ページ IR 座標への写像
// （Y反転＋四半回転4方位）を検査する。実機（AutoCAD＋PDFドライバ）との
// 数値突き合わせは別途実施済み。
using System;
using CadPeta.Geometry;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class PlotWindowMapperTests
{
    const int Places = 9; // 検証桁（純粋数学なので浮動小数の丸めのみ吸収する）

    static void AssertRect(RectMm expected, RectMm actual)
    {
        Assert.Equal(expected.X0, actual.X0, Places);
        Assert.Equal(expected.Y0, actual.Y0, Places);
        Assert.Equal(expected.X1, actual.X1, Places);
        Assert.Equal(expected.Y1, actual.Y1, Places);
    }

    [Fact]
    public void Fit尺度は窓と印刷可能領域の縦横比で小さい方を採る()
    {
        // 窓 200×100（図面単位）を印刷可能 400×280mm へ → 幅基準 2.0 と高さ基準 2.8 の小さい方
        Assert.Equal(2.0, PlotWindowMapper.FitScale(200, 100, 400, 280), Places);
        // 縦長窓は高さ基準が効く
        Assert.Equal(2.8, PlotWindowMapper.FitScale(50, 100, 400, 280), Places);
    }

    [Fact]
    public void Fit尺度は縮退した窓と領域を拒否する()
    {
        Assert.Throws<ArgumentException>(() => PlotWindowMapper.FitScale(0, 100, 400, 280));
        Assert.Throws<ArgumentException>(() => PlotWindowMapper.FitScale(200, 100, 400, 0));
    }

    [Fact]
    public void 回転なしは中央配置とY反転だけを行う()
    {
        // 用紙 420×297、印刷可能領域中心 (210, 148.5)（対称マージン）へ 100×60 を配置
        var rect = PlotWindowMapper.MapCenteredWindow(
            100, 60, 210, 148.5, 420, 297, quarterTurnsCw: 0);
        AssertRect(new RectMm(160, 118.5, 260, 178.5), rect);
    }

    [Fact]
    public void 回転なしで非対称な印刷可能領域の中心を保つ()
    {
        // 中心 (200, 140)（マージン非対称）: ページ座標では Y 反転して (200, 157)
        var rect = PlotWindowMapper.MapCenteredWindow(
            100, 60, 200, 140, 420, 297, quarterTurnsCw: 0);
        AssertRect(new RectMm(150, 127, 250, 187), rect);
    }

    [Fact]
    public void 時計回り90度はページ寸法だけ入れ替え内容寸法は表示フレームのまま保つ()
    {
        // 用紙 420×297 の中心へ 100×60 → 表示ページは 297×420、内容は 100×60 のまま
        // （accoreconsole 実機の rot73=1 実測: 窓 100×70 は回転後も 100×70）
        var rect = PlotWindowMapper.MapCenteredWindow(
            100, 60, 210, 148.5, 420, 297, quarterTurnsCw: 1);
        AssertRect(new RectMm(148.5 - 50, 210 - 30, 148.5 + 50, 210 + 30), rect);
    }

    [Fact]
    public void 時計回り90度は非対称中心を正しく回す()
    {
        // 用紙座標の中心 (100, 50) → ページ座標 (100, 247) → cw90 で (297-247, 100) = (50, 100)
        var rect = PlotWindowMapper.MapCenteredWindow(
            20, 10, 100, 50, 420, 297, quarterTurnsCw: 1);
        AssertRect(new RectMm(50 - 10, 100 - 5, 50 + 10, 100 + 5), rect);
    }

    [Fact]
    public void 回転180度は中心を対角へ写す()
    {
        // 用紙座標の中心 (100, 50) → ページ座標 (100, 247) → 180 度で (320, 50)
        var rect = PlotWindowMapper.MapCenteredWindow(
            20, 10, 100, 50, 420, 297, quarterTurnsCw: 2);
        AssertRect(new RectMm(320 - 10, 50 - 5, 320 + 10, 50 + 5), rect);
    }

    [Fact]
    public void 回転270度は90度の逆へ写す()
    {
        // ページ座標 (100, 247) → cw270（=ccw90）で (247, 420-100) = (247, 320)
        var rect = PlotWindowMapper.MapCenteredWindow(
            20, 10, 100, 50, 420, 297, quarterTurnsCw: 3);
        AssertRect(new RectMm(247 - 10, 320 - 5, 247 + 10, 320 + 5), rect);
    }

    [Fact]
    public void 回転4回で恒等に戻る()
    {
        var r0 = PlotWindowMapper.MapCenteredWindow(100, 60, 200, 140, 420, 297, 0);
        var r4 = PlotWindowMapper.MapCenteredWindow(100, 60, 200, 140, 420, 297, 4);
        AssertRect(r0, r4);
    }

    [Fact]
    public void 縮退した内容矩形と用紙を拒否する()
    {
        Assert.Throws<ArgumentException>(
            () => PlotWindowMapper.MapCenteredWindow(0, 60, 210, 148.5, 420, 297, 0));
        Assert.Throws<ArgumentException>(
            () => PlotWindowMapper.MapCenteredWindow(100, 60, 210, 148.5, 420, 0, 0));
    }
}
