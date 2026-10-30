// 数値の判定の共通関数（内核）。netstandard2.0 には double.IsFinite が無いため、
// 各層で同じ判定を書き写さないようにこの 1 か所へ置く。
namespace CadPeta.Geometry;

public static class Numbers
{
    /// <summary>NaN でも無限大でもない値か。</summary>
    public static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>有限で 0 より大きい値か（用紙の寸法など、長さとして使える値）。</summary>
    public static bool IsPositiveFinite(double value) => IsFinite(value) && value > 0;
}
