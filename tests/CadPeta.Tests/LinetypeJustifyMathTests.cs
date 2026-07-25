// 破線パターンの端部整列（パターンジャスティフィケーション）の単体テスト。
// 期待値は tests/tools/cad-typography/linetype_symbol_check/justify_derive/ の実測導出（accoreconsole で
// 生成した DWG を -PLOT→CLI で SVG 化し、破線片の端点座標を実測）に基づく。
// JP15=dash10/gap5（P=15）・JP24=dash12/gap5/dash6/gap1（P=24）は導出専用の単純パターン。
// 許容誤差 0.05mm は実測ノイズ（PDF数値精度・浮動小数）の範囲を包含する。
using CadPeta.Acquire.Common;
using Xunit;

namespace CadPeta.Tests;

public class LinetypeJustifyMathTests
{
    static readonly double[] Jp15 = { 10, -5 };
    static readonly double[] Jp24 = { 12, -5, 6, -1 };

    const double TolDu = 0.05; // 実測許容誤差（mm、図面単位=mmのテストのため共用）

    // ---------- JustifyOpen: 導出セット（JP15） ----------

    [Theory]
    // L, 期待する先頭要素の値（半端片。null なら実線）
    [InlineData(3, null)]
    [InlineData(7, null)]
    [InlineData(10, null)]
    [InlineData(13, null)]
    [InlineData(15, 5.0)]
    [InlineData(20, 7.5)]
    [InlineData(22.5, 8.75)]
    [InlineData(30, 5.0)]
    [InlineData(37, 8.5)]
    [InlineData(45, 5.0)]
    [InlineData(50, 7.5)]
    [InlineData(60, 5.0)]
    [InlineData(70, 10.0)]
    [InlineData(100, 10.0)]
    [InlineData(142, 8.5)]
    public void JP15_導出セットの先頭半端片が実測と一致する(double lengthDu, double? expectedHalf)
    {
        var result = LinetypeJustifyMath.JustifyOpen(Jp15, lengthDu);
        if (expectedHalf == null)
        {
            Assert.Null(result); // 1周期未満は実線
            return;
        }
        Assert.NotNull(result);
        Assert.Equal(expectedHalf.Value, result![0], TolDu);
        Assert.Equal(expectedHalf.Value, result[result.Length - 1], TolDu); // 両端対称
        Assert.Equal(lengthDu, result.Sum(Math.Abs), TolDu);  // 総和が曲線長と一致
    }

    // ---------- JustifyOpen: ホールドアウトセット（導出に使っていない L） ----------

    [Theory]
    [InlineData(5, null)]
    [InlineData(18, 6.5)]
    [InlineData(26, 10.5)]     // ρ=11 > 実線長10 でも半端片は名目実線長を超えて良い（実測確認済み）
    [InlineData(33, 6.5)]
    [InlineData(48, 6.5)]
    [InlineData(65, 7.5)]
    [InlineData(118, 11.5)]
    public void JP15_ホールドアウトの先頭半端片が予測と一致する(double lengthDu, double? expectedHalf)
    {
        var result = LinetypeJustifyMath.JustifyOpen(Jp15, lengthDu);
        if (expectedHalf == null)
        {
            Assert.Null(result);
            return;
        }
        Assert.NotNull(result);
        Assert.Equal(expectedHalf.Value, result![0], TolDu);
        Assert.Equal(expectedHalf.Value, result[result.Length - 1], TolDu);
        Assert.Equal(lengthDu, result.Sum(Math.Abs), TolDu);
    }

    // ---------- JustifyOpen: 非対称パターン（JP24、複数実線要素） ----------

    [Theory]
    [InlineData(24, 6.0)]
    [InlineData(36, 12.0)]
    [InlineData(50, 7.0)]
    [InlineData(80, 10.0)]
    [InlineData(100, 8.0)]
    public void JP24_非対称パターンでも0番要素だけが両端で分割される(double lengthDu, double expectedHalf)
    {
        var result = LinetypeJustifyMath.JustifyOpen(Jp24, lengthDu);
        Assert.NotNull(result);
        Assert.Equal(expectedHalf, result![0], TolDu);
        Assert.Equal(expectedHalf, result[result.Length - 1], TolDu);
        Assert.Equal(lengthDu, result.Sum(Math.Abs), TolDu);
    }

    [Fact]
    public void JP24_中間要素は名目長のまま変わらない()
    {
        // L=50 → n=2, ρ=2, 半端片=7。中間には e[1..3] の1回分＋(n-1=1)回の e[0..3] 全体が入り、
        // 実測（parse_circles/parse_lines と同型の parse_lines.py 実行結果）では
        // 実線列 [7, 6, 12, 6, 7] となる（gap は -5,-1,-5,-1）
        var result = LinetypeJustifyMath.JustifyOpen(Jp24, 50)!;
        var dashes = result.Where(v => v > 0).ToArray();
        Assert.Equal(new[] { 7.0, 6.0, 12.0, 6.0, 7.0 }, dashes, new ApproxComparer(TolDu));
        var gaps = result.Where(v => v < 0).Select(Math.Abs).ToArray();
        Assert.Equal(new[] { 5.0, 1.0, 5.0, 1.0 }, gaps, new ApproxComparer(TolDu));
    }

    // ---------- JustifyClosed: 導出セット（円周、JP15） ----------

    [Theory]
    // 目標円周C, 期待する周期数n, 期待するスケール後の実線長（=e[0]*k）
    [InlineData(40, 3, 8.8889)]
    [InlineData(45, 3, 10.0)]
    [InlineData(50, 3, 11.1111)]
    [InlineData(60, 4, 10.0)]
    [InlineData(70, 5, 9.3333)]
    [InlineData(100, 7, 9.5238)]
    [InlineData(110, 7, 10.4762)]
    public void JP15_円周の導出セットで周期数と伸縮後の実線長が一致する(double circumferenceDu, int expectedN, double expectedDash)
    {
        var result = LinetypeJustifyMath.JustifyClosed(Jp15, circumferenceDu);
        Assert.NotNull(result);
        Assert.Equal(expectedDash, result![0], TolDu);
        double k = expectedDash / Jp15[0];
        Assert.Equal(circumferenceDu, expectedN * (Jp15[0] + Math.Abs(Jp15[1])) * k, TolDu);
    }

    // ---------- JustifyClosed: ホールドアウト ----------

    [Theory]
    [InlineData(55, 9.1667)]
    [InlineData(85, 9.4444)]
    [InlineData(130, 9.6296)]
    public void JP15_円周のホールドアウトで伸縮後の実線長が予測と一致する(double circumferenceDu, double expectedDash)
    {
        var result = LinetypeJustifyMath.JustifyClosed(Jp15, circumferenceDu);
        Assert.NotNull(result);
        Assert.Equal(expectedDash, result![0], TolDu);
    }

    [Fact]
    public void 円周が半周期未満なら実線として扱う()
    {
        // round(C/P) が 0 になるほど小さい円（P=15 に対し C=5 → round(0.33)=0）
        Assert.Null(LinetypeJustifyMath.JustifyClosed(Jp15, 5));
    }

    [Fact]
    public void 直線が1周期未満なら実線として扱う()
    {
        Assert.Null(LinetypeJustifyMath.JustifyOpen(Jp15, 14.999));
    }

    [Fact]
    public void パターン長が0以下なら実線として扱う()
    {
        Assert.Null(LinetypeJustifyMath.JustifyOpen(Array.Empty<double>(), 10));
        Assert.Null(LinetypeJustifyMath.JustifyClosed(new[] { 0.0 }, 10));
    }

    // ---------- SymbolOccurrences ----------

    [Fact]
    public void 記号要素の出現位置は整列シフトを反映する()
    {
        // JP24 の要素2（solid_b=6）の始点（周期内オフセット=|e0|+|e1|=17）に付随する記号。
        // L=50, n=2。実測（justify_derive/parse_lines.py で L=50 行の各実線片の始点を
        // 図面距離へ換算）: solid_b の2回の出現はそれぞれ距離12・36から始まる
        // （実測ログの片 [10.66,12.66]svg・[18.66,20.66]svg を較正済みアフィンで
        // 図面距離へ変換した値）。
        // 代数的にも一致する: シフト=half-e[0]=7-12=-5。
        // 出現位置 = shift + period*P + withinPeriodOffsetDu = -5+period*24+17 = 12, 36
        var xs = LinetypeJustifyMath.SymbolOccurrences(Jp24, segLenDu: 50, withinPeriodOffsetDu: 17).ToList();
        Assert.Equal(new[] { 12.0, 36.0 }, xs, new ApproxComparer(TolDu));
    }

    [Theory]
    // L, 期待する出現距離列（JP15 の要素0=dash10 自身に記号が付く場合。実測は
    // justify_derive/sym0/（自作 JP15SYM 線種、TRACKS と同型＝記号が分割対象の要素0にある）。
    // 出現位置 = half + period*P（withinPeriodOffsetDu=|e0|=10 のとき shift+within=half になる）
    [InlineData(15, new[] { 5.0 })]
    [InlineData(20, new[] { 7.5 })]
    [InlineData(26, new[] { 10.5 })]
    [InlineData(37, new[] { 8.5, 23.5 })]
    [InlineData(45, new[] { 5.0, 20.0, 35.0 })]
    [InlineData(60, new[] { 5.0, 20.0, 35.0, 50.0 })]
    [InlineData(100, new[] { 10.0, 25.0, 40.0, 55.0, 70.0, 85.0 })]
    public void 分割対象の0番要素自身に付随する記号も実測どおりの位置になる(double lengthDu, double[] expected)
    {
        var xs = LinetypeJustifyMath.SymbolOccurrences(Jp15, segLenDu: lengthDu, withinPeriodOffsetDu: Jp15[0]).ToList();
        Assert.Equal(expected, xs, new ApproxComparer(TolDu));
    }

    [Fact]
    public void 区間が短く1周期に満たない場合は空を返す()
    {
        var xs = LinetypeJustifyMath.SymbolOccurrences(Jp24, segLenDu: 10, withinPeriodOffsetDu: 17).ToList();
        Assert.Empty(xs);
    }

    [Fact]
    public void 出現位置が区間外に落ちる回は除外される()
    {
        // withinPeriodOffsetDu が区間長を超えるケースでも例外にならず単に除外する
        var xs = LinetypeJustifyMath.SymbolOccurrences(Jp15, segLenDu: 26, withinPeriodOffsetDu: 30).ToList();
        Assert.Empty(xs);
    }

    sealed class ApproxComparer : IEqualityComparer<double>
    {
        readonly double tol;
        public ApproxComparer(double tol) => this.tol = tol;
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tol;
        public int GetHashCode(double obj) => 0;
    }
}
