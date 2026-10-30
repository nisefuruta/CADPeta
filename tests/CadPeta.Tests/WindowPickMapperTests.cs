// 窓 pick の座標写像の単体テスト。
//
// 期待値は「AutoCAD 2026 実機が出した値」に固定する。CAD 自身の trans が返した DCS と、
// CAD 自身の -PLOT がページ設定へ格納した窓（DXF 群 48/49/140/141）が一致した実測値であり、
// 再現手順は tests/tools/regression/window_contract にある。
// 数式をこちらの理解で書き直した期待値にすると、取り違えたまま緑になる（実際に一度そうなった）。
using System;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class WindowPickMapperTests
{
    /// <summary>実測値との突き合わせ許容差。実機が出力した小数6桁の丸めを吸収する。</summary>
    const double Tolerance = 1e-5;

    /// <summary>解析的に求めた直交座標の期待値に使う許容差。</summary>
    const double MathTolerance = 1e-10;

    /// <summary>3-4-5 の角（cos=0.8 / sin=0.6）。計測ケースの回転角と同一。</summary>
    static readonly double RotRad = Math.Atan2(0.6, 0.8);

    static Vec3 V(double x, double y, double z = 0) => new(x, y, z);

    static WindowPickFrame Frame(Vec3 origin, Vec3 xAxis, Vec3 yAxis, Vec3 zAxis,
                                 Vec3 target, Vec3 dir, double twistRad) =>
        new(origin, xAxis, yAxis, zAxis, target, dir, twistRad);

    /// <summary>世界 UCS のフレーム（Z 軸は X×Y）。</summary>
    static WindowPickFrame WorldUcs(Vec3 target, Vec3 dir, double twistRad) =>
        Frame(V(0, 0, 0), V(1, 0, 0), V(0, 1, 0), V(0, 0, 1), target, dir, twistRad);

    /// <summary>実機の実測値との突き合わせ(許容差 <see cref="Tolerance"/>)。</summary>
    static void AssertWindow(WindowPickResult r, double x0, double y0, double x1, double y1) =>
        AssertWindowWithin(r, Tolerance, x0, y0, x1, y1);

    /// <summary>解析的に求めた期待値との突き合わせ(許容差 <see cref="MathTolerance"/>)。</summary>
    static void AssertMathWindow(WindowPickResult r, double x0, double y0, double x1, double y1) =>
        AssertWindowWithin(r, MathTolerance, x0, y0, x1, y1);

    static void AssertWindowWithin(WindowPickResult r, double tolerance, double x0, double y0, double x1, double y1)
    {
        Assert.Equal(WindowPickOutcome.Ok, r.Outcome);
        Assert.Equal(x0, r.Window.MinX, tolerance);
        Assert.Equal(y0, r.Window.MinY, tolerance);
        Assert.Equal(x1, r.Window.MaxX, tolerance);
        Assert.Equal(y1, r.Window.MaxY, tolerance);
    }

    static (Vec3 XAxis, Vec3 YAxis) QuadrantAxes(int degrees) => degrees switch
    {
        0 or 360 => (V(1, 0, 0), V(0, 1, 0)),
        90 => (V(0, 1, 0), V(-1, 0, 0)),
        180 => (V(-1, 0, 0), V(0, -1, 0)),
        270 => (V(0, -1, 0), V(1, 0, 0)),
        _ => throw new ArgumentOutOfRangeException(nameof(degrees)),
    };

    static WindowPickResult Map(WindowPickFrame frame) =>
        WindowPickMapper.ToPlotWindow(V(50, 40), V(150, 110), frame, WindowPickContract.Dcs);

    [Fact]
    public void 対照_世界UCS_目標点0_ねじれ0_では_pick値がそのまま窓になる()
    {
        // 計測ケース A: DCS=UCS=WCS が一致する条件。既存のテストチャートがこの条件に偏っていたため、
        // 座標系の取り違えが長く露見しなかった
        AssertWindow(Map(WorldUcs(V(0, 0, 0), V(0, 0, 1), 0)), 50, 40, 150, 110);
    }

    [Fact]
    public void 目標点が非ゼロなら_窓は目標点ぶん差し引かれる()
    {
        // 計測ケース B: 実機の trans DCS=(-10,-10),(90,60)。-PLOT の格納値 48/49/140/141 とも一致。
        // ここが従来 UCS→WCS のままだったため、パン/ズームで目標点が動いた図面が壊れていた
        AssertWindow(Map(WorldUcs(V(60, 50, 0), V(0, 0, 1), 0)), -10, -10, 90, 60);
    }

    [Fact]
    public void UCS回転とねじれがある図面でも_実機の値と一致する()
    {
        // 計測ケース E: 実機の trans DCS=(-24.4,59.2),(-63.6,174.8)
        var frame = Frame(V(0, 0, 0), V(0.8, 0.6, 0), V(-0.6, 0.8, 0), V(0, 0, 1),
                          V(0, 0, 0), V(0, 0, 1), RotRad);
        AssertWindow(Map(frame), -63.6, 59.2, -24.4, 174.8);
    }

    [Fact]
    public void UCS原点移動と回転と目標点が重なっても_実機の値と一致する()
    {
        // 計測ケース G（実物図面に相当する条件）: 実機の trans DCS=(83,21),(43.8,136.6)
        var frame = Frame(V(123, -45, 0), V(0.8, 0.6, 0), V(-0.6, 0.8, 0), V(0, 0, 1),
                          V(60, 50, 0), V(0, 0, 1), RotRad);
        AssertWindow(Map(frame), 43.8, 21, 83, 136.6);
    }

    [Fact]
    public void ビューがねじれているだけでも_実機の値と一致する()
    {
        // 計測ケース H: ねじれ15度。実機の trans DCS=(37.94353,51.577985),(116.418779,145.074698)
        var frame = WorldUcs(V(0, 0, 0), V(0, 0, 1), 15 * Math.PI / 180.0);
        AssertWindow(Map(frame), 37.94353, 51.577985, 116.418779, 145.074698);
    }

    [Theory]
    // 原点から相対 (3,4),(-5,-2) の非対称な2点を使い、min/max 後も回転方向を識別する。
    // 期待値は直交座標の既知値であり、製品コードの式から生成しない。
    [InlineData(0, -5, -2, 3, 4)]
    [InlineData(45, -2.121320343559643, -4.949747468305833, -0.7071067811865476, 4.949747468305833)]
    [InlineData(90, -4, -5, 2, 3)]
    [InlineData(135, -4.949747468305833, -2.121320343559643, 4.949747468305833, -0.7071067811865476)]
    [InlineData(180, -3, -4, 5, 2)]
    [InlineData(225, 0.7071067811865476, -4.949747468305833, 2.121320343559643, 4.949747468305833)]
    [InlineData(270, -2, -3, 4, 5)]
    [InlineData(315, -4.949747468305833, 0.7071067811865476, 4.949747468305833, 2.121320343559643)]
    [InlineData(360, -5, -2, 3, 4)]
    [InlineData(-90, -2, -3, 4, 5)]
    [InlineData(450, -4, -5, 2, 3)]
    public void ViewTwistは_象限内部と境界と周期を正しいDCSへ写す(
        double degrees, double minX, double minY, double maxX, double maxY)
    {
        var frame = WorldUcs(V(10, 20, 0), V(0, 0, 1), degrees * Math.PI / 180.0);
        var r = WindowPickMapper.ToPlotWindow(V(13, 24), V(5, 18), frame, WindowPickContract.Dcs);

        AssertMathWindow(r, minX, minY, maxX, maxY);
    }

    [Theory]
    // UCS 原点 (10,20)、UCS 点 (3,4),(-5,-2)。DCS の目標点は WCS (1,-2)。
    // UCS→WCS と、その後の WCS→DCS（目標点差引き）を別々の期待値で固定する。
    [InlineData(0, 5, 18, 13, 24, 4, 20, 12, 26)]
    [InlineData(90, 6, 15, 12, 23, 5, 17, 11, 25)]
    [InlineData(180, 7, 16, 15, 22, 6, 18, 14, 24)]
    [InlineData(270, 8, 17, 14, 25, 7, 19, 13, 27)]
    [InlineData(360, 5, 18, 13, 24, 4, 20, 12, 26)]
    public void UCS回転の象限境界で_UCSからWCSとDCSへの各段階が正しい(
        int degrees,
        double wcsMinX, double wcsMinY, double wcsMaxX, double wcsMaxY,
        double dcsMinX, double dcsMinY, double dcsMaxX, double dcsMaxY)
    {
        var (xAxis, yAxis) = QuadrantAxes(degrees);
        var frame = Frame(V(10, 20, 0), xAxis, yAxis, V(0, 0, 1),
                          V(1, -2, 0), V(0, 0, 1), 0);

        var wcs = WindowPickMapper.ToPlotWindow(V(3, 4), V(-5, -2), frame, WindowPickContract.UcsToWcs);
        var dcs = WindowPickMapper.ToPlotWindow(V(3, 4), V(-5, -2), frame, WindowPickContract.Dcs);

        AssertMathWindow(wcs, wcsMinX, wcsMinY, wcsMaxX, wcsMaxY);
        AssertMathWindow(dcs, dcsMinX, dcsMinY, dcsMaxX, dcsMaxY);
    }

    [Theory]
    // UCS 回転と ViewTwist の境界角を直積で検査する。期待値は90度ごとの符号置換で
    // 独立算出した整数値で、両回転の符号違い・適用順違い・相殺を検出できる。
    [InlineData(0, 0, -18, -57, -13, -49)]
    [InlineData(0, 90, 49, -18, 57, -13)]
    [InlineData(0, 180, 13, 49, 18, 57)]
    [InlineData(0, 270, -57, 13, -49, 18)]
    [InlineData(0, 360, -18, -57, -13, -49)]
    [InlineData(90, 0, -31, -58, -23, -53)]
    [InlineData(90, 90, 53, -31, 58, -23)]
    [InlineData(90, 180, 23, 53, 31, 58)]
    [InlineData(90, 270, -58, 23, -53, 31)]
    [InlineData(90, 360, -31, -58, -23, -53)]
    [InlineData(180, 0, -27, -71, -22, -63)]
    [InlineData(180, 90, 63, -27, 71, -22)]
    [InlineData(180, 180, 22, 63, 27, 71)]
    [InlineData(180, 270, -71, 22, -63, 27)]
    [InlineData(180, 360, -27, -71, -22, -63)]
    [InlineData(270, 0, -17, -67, -9, -62)]
    [InlineData(270, 90, 62, -17, 67, -9)]
    [InlineData(270, 180, 9, 62, 17, 67)]
    [InlineData(270, 270, -67, 9, -62, 17)]
    [InlineData(270, 360, -17, -67, -9, -62)]
    [InlineData(360, 0, -18, -57, -13, -49)]
    [InlineData(360, 90, 49, -18, 57, -13)]
    [InlineData(360, 180, 13, 49, 18, 57)]
    [InlineData(360, 270, -57, 13, -49, 18)]
    [InlineData(360, 360, -18, -57, -13, -49)]
    public void UCS回転とViewTwistの象限境界25通りを直積で検査する(
        int ucsDegrees, int twistDegrees,
        double minX, double minY, double maxX, double maxY)
    {
        var (xAxis, yAxis) = QuadrantAxes(ucsDegrees);
        var frame = Frame(V(10, -20, 0), xAxis, yAxis, V(0, 0, 1),
                          V(30, 40, 0), V(0, 0, 1), twistDegrees * Math.PI / 180.0);

        var r = WindowPickMapper.ToPlotWindow(V(2, 3), V(7, 11), frame, WindowPickContract.Dcs);

        AssertMathWindow(r, minX, minY, maxX, maxY);
    }

    [Fact]
    public void 傾いたUCSでも_その面を正面から見ていれば_pick値がそのまま窓になる()
    {
        // 計測ケース K: UCS を X 軸まわりに傾け、その面へ平面視。実機の trans DCS=(50,40),(150,110)
        var frame = Frame(V(0, 0, 0), V(1, 0, 0), V(0, 0.8, 0.6), V(0, -0.6, 0.8),
                          V(0, 0, 0), V(0, -0.6, 0.8), 0);
        AssertWindow(Map(frame), 50, 40, 150, 110);
    }

    [Fact]
    public void 三次元ビューでも_実機の値と一致する()
    {
        // 計測ケース L: 視線 (1,1,1)。実機の trans DCS=(-7.071068,-36.742346),(-28.284271,-106.144556)。
        // 任意軸アルゴリズムの基底が CAD と一致していることを固定する
        var frame = WorldUcs(V(0, 0, 0), V(1, 1, 1), 0);
        AssertWindow(Map(frame), -28.284271, -106.144556, -7.071068, -36.742346);
    }

    [Fact]
    public void 三次元ビューと90度ねじれの組合せでも_任意軸基底上で正しく回転する()
    {
        // ケース L の実機値を DCS 上で90度回した独立期待値。
        // 任意軸基底の選択とロドリゲス回転を同時に通す。
        var frame = WorldUcs(V(0, 0, 0), V(1, 1, 1), Math.PI / 2);
        AssertWindow(Map(frame), 36.742346, -28.284271, 106.144556, -7.071068);
    }

    [Fact]
    public void 負のZ方向から見たDCSは_CADの任意軸規則に従いX軸だけ反転する()
    {
        var frame = WorldUcs(V(0, 0, 0), V(0, 0, -1), 0);
        var r = WindowPickMapper.ToPlotWindow(V(3, 4), V(-5, -2), frame, WindowPickContract.Dcs);
        AssertMathWindow(r, -3, -2, 5, 4);
    }

    [Theory]
    // DXF の任意軸アルゴリズムは |nx|,|ny| がともに 1/64 未満のときだけ世界Yを基準にする。
    // 直前・一致・直後で基底が切り替わることを固定する。
    [InlineData(0.015624, 49.99389689312086, 40, 149.9816906793626, 110)]
    [InlineData(0.015625, 40, -149.98168833540146, 110, -49.99389611180049)]
    [InlineData(0.015626, 40, -149.98168599129028, 110, -49.99389533043009)]
    public void 任意軸アルゴリズムの1_64境界で_CADと同じDCS基底を選ぶ(
        double normalX, double minX, double minY, double maxX, double maxY)
    {
        var normalZ = Math.Sqrt(1 - normalX * normalX);
        var frame = WorldUcs(V(0, 0, 0), V(normalX, 0, normalZ), 0);

        var r = WindowPickMapper.ToPlotWindow(V(50, 40), V(150, 110), frame, WindowPickContract.Dcs);

        AssertMathWindow(r, minX, minY, maxX, maxY);
    }

    [Fact]
    public void 傾いたUCSと三次元ビューの組合せでも_実機の値と一致する()
    {
        // 計測ケース M: 実機の trans DCS=(-12.727922,-13.880442),(-43.84062,-43.274319)
        var frame = Frame(V(0, 0, 0), V(1, 0, 0), V(0, 0.8, 0.6), V(0, -0.6, 0.8),
                          V(0, 0, 0), V(1, 1, 1), 0);
        AssertWindow(Map(frame), -43.84062, -43.274319, -12.727922, -13.880442);
    }

    [Fact]
    public void UCS平面を真横から見ている場合は_誤った窓を返さず縮退として報告する()
    {
        // 計測ケース N: 視線が UCS 平面に含まれ、pick 点が DCS 上の1直線へ潰れる。
        // 実機では CAD 自身が eInvalidWindowArea で窓プロットを拒否し、-PLOT も中止した
        var frame = Frame(V(0, 0, 0), V(1, 0, 0), V(0, 0.8, 0.6), V(0, -0.6, 0.8),
                          V(0, 0, 0), V(0, 0.8, 0.6), 0);
        var r = WindowPickMapper.ToPlotWindow(V(50, 40), V(150, 110), frame, WindowPickContract.Dcs);
        Assert.Equal(WindowPickOutcome.ViewPlaneDegenerate, r.Outcome);
    }

    [Theory]
    [InlineData(0, WindowPickOutcome.ViewPlaneDegenerate)]
    [InlineData(0.0000000000005, WindowPickOutcome.ViewPlaneDegenerate)]
    [InlineData(0.000000000001, WindowPickOutcome.Ok)]
    [InlineData(0.000000000002, WindowPickOutcome.Ok)]
    public void UCS平面の真横判定は_許容差の直前と境界と直後を区別する(
        double normalComponent, WindowPickOutcome expected)
    {
        var viewDirection = V(Math.Sqrt(1 - normalComponent * normalComponent), 0, normalComponent);
        var frame = WorldUcs(V(0, 0, 0), viewDirection, 0);

        var r = WindowPickMapper.ToPlotWindow(V(1, 2), V(3, 5), frame, WindowPickContract.Dcs);

        Assert.Equal(expected, r.Outcome);
    }

    [Theory]
    // 利用者がどの向きへドラッグしても同じ窓になること（4象限すべて）
    [InlineData(50, 40, 150, 110)]   // 右上へ
    [InlineData(150, 110, 50, 40)]   // 左下へ
    [InlineData(150, 40, 50, 110)]   // 左上へ
    [InlineData(50, 110, 150, 40)]   // 右下へ
    public void ドラッグの向きに依らず同じ窓になる(double x1, double y1, double x2, double y2)
    {
        var frame = WorldUcs(V(60, 50, 0), V(0, 0, 1), 0);
        var r = WindowPickMapper.ToPlotWindow(V(x1, y1), V(x2, y2), frame, WindowPickContract.Dcs);
        AssertWindow(r, -10, -10, 90, 60);
    }

    [Fact]
    public void 診断用のUcsToWcs契約では_DCS変換を行わない()
    {
        // 全製品の実機契約は DCS。これは UCS→WCS 段階だけを独立検査する診断用の選択肢。
        // UCS 回転 36.87 度・原点移動なしで WCS=(16,62),(54,178)（実機の trans と同値）
        var frame = Frame(V(0, 0, 0), V(0.8, 0.6, 0), V(-0.6, 0.8, 0), V(0, 0, 1),
                          V(60, 50, 0), V(0, 0, 1), RotRad);
        var r = WindowPickMapper.ToPlotWindow(V(50, 40), V(150, 110), frame, WindowPickContract.UcsToWcs);
        AssertWindow(r, 16, 62, 54, 178);
    }

    [Fact]
    public void 長さ0の視線方向は_写像が定義されないため例外にする()
    {
        var frame = WorldUcs(V(0, 0, 0), V(0, 0, 0), 0);
        Assert.Throws<ArgumentException>(() =>
            WindowPickMapper.ToPlotWindow(V(50, 40), V(150, 110), frame, WindowPickContract.Dcs));
    }

    [Fact]
    public void UCS基底が正規直交右手系でなければ_誤った窓を返さず例外にする()
    {
        var invalidFrames = new[]
        {
            Frame(V(0, 0, 0), V(0, 0, 0), V(0, 1, 0), V(0, 0, 1), V(0, 0, 0), V(0, 0, 1), 0),
            Frame(V(0, 0, 0), V(1, 0, 0), V(1, 0, 0), V(0, 0, 1), V(0, 0, 0), V(0, 0, 1), 0),
            Frame(V(0, 0, 0), V(2, 0, 0), V(0, 1, 0), V(0, 0, 1), V(0, 0, 0), V(0, 0, 1), 0),
            Frame(V(0, 0, 0), V(1, 0, 0), V(0, 1, 0), V(0, 0, -1), V(0, 0, 0), V(0, 0, 1), 0),
        };

        foreach (var frame in invalidFrames)
        {
            Assert.Throws<ArgumentException>(() =>
                WindowPickMapper.ToPlotWindow(V(1, 2), V(3, 4), frame, WindowPickContract.Dcs));
        }
    }

    [Fact]
    public void 非有限値を含む点やフレームは_Outcome_Okとして流出させない()
    {
        var valid = WorldUcs(V(0, 0, 0), V(0, 0, 1), 0);
        Assert.Throws<ArgumentException>(() =>
            WindowPickMapper.ToPlotWindow(V(double.NaN, 0), V(1, 1), valid, WindowPickContract.Dcs));
        Assert.Throws<ArgumentException>(() =>
            WindowPickMapper.ToPlotWindow(V(0, 0), V(double.PositiveInfinity, 1), valid, WindowPickContract.Dcs));

        var invalidTarget = WorldUcs(V(double.NaN, 0, 0), V(0, 0, 1), 0);
        Assert.Throws<ArgumentException>(() =>
            WindowPickMapper.ToPlotWindow(V(0, 0), V(1, 1), invalidTarget, WindowPickContract.Dcs));

        var invalidTwist = WorldUcs(V(0, 0, 0), V(0, 0, 1), double.NegativeInfinity);
        Assert.Throws<ArgumentException>(() =>
            WindowPickMapper.ToPlotWindow(V(0, 0), V(1, 1), invalidTwist, WindowPickContract.Dcs));
    }
}
