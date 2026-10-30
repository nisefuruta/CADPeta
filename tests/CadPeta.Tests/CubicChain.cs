using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;

namespace CadPeta.Tests;

/// <summary>試験が連結ベジェ列の通過点を確かめるための評価。</summary>
internal static class CubicChain
{
    /// <summary>連結ベジェ列(先頭が <see cref="IrMove"/>、以降が <see cref="IrCubic"/>)を一様パラメータ <paramref name="t01"/>(0〜1)で評価する(検証用の粗い評価)。</summary>
    public static (double X, double Y) Eval(List<IrSegment> segs, double t01)
    {
        var cubics = segs.OfType<IrCubic>().ToList();
        var start = (IrMove)segs[0];
        double scaled = t01 * cubics.Count;
        int i = Math.Min(cubics.Count - 1, (int)scaled);
        double t = scaled - i;
        (double X, double Y) p0 = i == 0 ? (start.X, start.Y) : (cubics[i - 1].X, cubics[i - 1].Y);
        var c = cubics[i];
        double u = 1 - t;
        return (u * u * u * p0.X + 3 * u * u * t * c.X1 + 3 * u * t * t * c.X2 + t * t * t * c.X,
                u * u * u * p0.Y + 3 * u * u * t * c.Y1 + 3 * u * t * t * c.Y2 + t * t * t * c.Y);
    }
}
