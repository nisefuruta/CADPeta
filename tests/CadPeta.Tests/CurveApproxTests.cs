// 構造SVG書き出し用の曲線近似の単体テスト。バルジ円弧は実図面の entget 値
// （TESTCHART2.dwg のポリライン、2026-07-09 実測）を正解に使う。
using System;
using System.Collections.Generic;
using CadPeta.Acquire.Common;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class CurveApproxTests
{
    static (double X, double Y) Identity(double x, double y) => (x, y);

    [Fact]
    public void バルジ円弧は実図面の弧と一致する()
    {
        // TESTCHART2.dwg のポリライン円弧区間（entget 実測）:
        // (121,241) → (136,236)、bulge=-1/3（時計回り・挟角73.74°）。
        // 中心 (125.1667, 228.5)・半径 13.1666 の円弧で、頂点は弦の上側（ドーム型）
        var segs = new List<IrSegment> { new IrMove(121, 241) };
        CurveApprox.BulgeArc(segs, Identity, 121, 241, 136, 236, -1.0 / 3.0);

        // 終点一致
        var last = (IrCubic)segs[segs.Count - 1];
        Assert.Equal(136, last.X, 9);
        Assert.Equal(236, last.Y, 9);

        // 弧上の点は中心から半径距離（円の方程式で全域を検査）。
        // 中心 = 弦中点(128.5,238.5) + 左法線×h、半径 = √(25/144+156.25)×…= 13.176157
        const double cx = 125.16666667, cy = 228.5, r = 13.17615672;
        for (double t = 0; t <= 1.0001; t += 0.1)
        {
            var p = CubicChain.Eval(segs, Math.Min(t, 1));
            double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            Assert.True(Math.Abs(d - r) < 0.01,
                $"t={t:F1} で円から {Math.Abs(d - r):F4} ずれた（ベジェ近似誤差の許容 0.01 超）");
        }

        // 弧の中点は弦より上（ドーム）= 中心・掃引方向の符号検証
        var mid = CubicChain.Eval(segs, 0.5);
        double chordYAtMid = (241 + 236) / 2.0;
        Assert.True(mid.Y > chordYAtMid,
            $"弧の中点 y={mid.Y:F2} が弦 y={chordYAtMid:F2} より下（膨らむ側が逆）");
    }

    [Fact]
    public void 正のバルジは反時計回りに掃引する()
    {
        // DXF 仕様: 正=反時計回り。(0,0)→(10,0) の進行で反時計回りの弧は下側を通る
        var segs = new List<IrSegment> { new IrMove(0, 0) };
        CurveApprox.BulgeArc(segs, Identity, 0, 0, 10, 0, 0.5);
        var mid = CubicChain.Eval(segs, 0.5);
        Assert.True(mid.Y < 0, $"反時計回りの弧の中点 y={mid.Y:F2} が上側（掃引が逆）");
        // サジッタ = b·弦/2 = 2.5
        Assert.Equal(-2.5, mid.Y, 2);
    }
}
