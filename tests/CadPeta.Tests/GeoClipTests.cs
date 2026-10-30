// 矩形クリップ幾何（内核 Geometry）の単体テスト。
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class GeoClipTests
{
    static readonly RectMm R = new(0, 0, 100, 100);

    [Fact]
    public void 全内のストロークは無変更で返す()
    {
        var segs = new List<IrSegment> { new IrMove(10, 10), new IrLine(90, 90) };
        var res = GeoClip.ClipStroke(segs, R, out bool modified, out _);
        Assert.Same(segs, res);
        Assert.False(modified);
    }

    [Fact]
    public void 境界交差の線分は境界で切断する()
    {
        var segs = new List<IrSegment> { new IrMove(50, 50), new IrLine(150, 50) };
        var res = GeoClip.ClipStroke(segs, R, out bool modified, out _);
        Assert.True(modified);
        Assert.NotNull(res);
        var end = Assert.IsType<IrLine>(res![res.Count - 1]); // ^1 は net48（System.Index 不在）で使えない
        Assert.Equal(100, end.X, 9);
        Assert.Equal(50, end.Y, 9);
    }

    [Fact]
    public void 全外のストロークはnullを返す()
    {
        var segs = new List<IrSegment> { new IrMove(200, 200), new IrLine(300, 300) };
        Assert.Null(GeoClip.ClipStroke(segs, R, out _, out _));
    }

    [Fact]
    public void 境界交差の塗りリングは交差多角形へ切る()
    {
        // (50,50)-(150,150) の正方形 → 交差は (50,50)-(100,100)
        var segs = new List<IrSegment>
        {
            new IrMove(50, 50), new IrLine(150, 50), new IrLine(150, 150),
            new IrLine(50, 150), new IrClose(),
        };
        var res = GeoClip.ClipFill(segs, R, out bool modified, out _);
        Assert.True(modified);
        Assert.NotNull(res);
        var b = GeoClip.Bbox(res!);
        Assert.Equal(new[] { 50.0, 50, 100, 100 }, b.Select(v => Math.Round(v, 9)).ToArray());
    }

    [Fact]
    public void 軸平行矩形のクリップ定義を認識する()
    {
        var rectSegs = new List<IrSegment>
        {
            new IrMove(10, 20), new IrLine(110, 20), new IrLine(110, 220),
            new IrLine(10, 220), new IrClose(),
        };
        Assert.True(GeoClip.IsAxisRect(rectSegs, out var r));
        Assert.Equal(new RectMm(10, 20, 110, 220), r);

        var diamond = new List<IrSegment>
        {
            new IrMove(50, 0), new IrLine(100, 50), new IrLine(50, 100),
            new IrLine(0, 50), new IrClose(),
        };
        Assert.False(GeoClip.IsAxisRect(diamond, out _));
    }
}
