// 破線パターンの端部整列（パターンジャスティフィケーション）の純粋計算部分。
// 特定のCAD製品APIに依存しない共有計算のため、取得アダプタ間で共有し単体テスト対象にする。
//
// 実測導出（tests/tools/cad-typography/linetype_symbol_check/justify_derive/、ホールドアウト検証込み）で
// 確定した規則:
//
// 開いた曲線（直線・円弧・楕円弧・スプライン・ポリライン全体〔Plinegen オン〕・
// ポリラインの各セグメント〔Plinegen オフ〕）:
//   パターンを signed 長さの列 e[0..m-1]（e[0]=最初の実線区間、以降 実線/間隙 交互、
//   絶対値の総和=P）とし、曲線長 L に対して n=floor(L/P) を求める。
//   n=0（L が1周期に満たない）なら破線を描かず実線として扱う（null を返す）。
//   n>=1 なら 余り ρ=L-n*P を e[0] の両端へ均等に配分する:
//     半端片 = e[0]/2 + ρ/2
//     出力配列 = [半端片, e[1..m-1]] に続けて (e[0..m-1]) を (n-1) 回連結し、
//               最後に半端片をもう一つ追加する。
//   中間の実線・間隙（e[0] 以外）は名目長のまま変わらない。
//   直線 L=3..142・円弧（半径30・90°）の27ケース（ホールドアウト7ケース込み）で
//   ±0.03mm の代数一致を実測確認済み。
//
// 閉じた曲線（円・閉ポリライン・閉楕円）:
//   周長 C に対して n=round(C/P)（最近接整数、四捨五入）を求め、
//   n=0 なら実線として扱う。n>=1 なら倍率 k=C/(n*P) をパターンの全要素へ一様に掛ける
//   （名目パターンを整数周期ちょうどへ伸縮）。円周のホールドアウト3ケース含む10ケースで
//   ±0.02mm の代数一致を実測確認済み（n は floor ではなく round＝最近接整数と実測確定。
//   例: 目標円周40mm・P=15mm→ n=3〔floor なら2〕）。
namespace CadPeta.Acquire.Common;

public static class LinetypeJustifyMath
{
    /// <summary>浮動小数の丸みで周期境界ちょうどの値が1周期分ずれて floor/round されることを
    /// 防ぐための許容（図面単位）。</summary>
    const double EpsDu = AcquireDefaults.LinetypeSymbolDistanceEpsilonDu;

    /// <summary>
    /// 開いた曲線の端部整列。pattern は e[0] が正の実線長、以降 実線/間隙 が交互の signed 長さ列。
    /// 戻り値は整列済みの signed 長さ列（stroke-dasharray にそのまま使える）。
    /// 曲線長が1周期に満たない場合は null（実線として描く）。
    /// </summary>
    public static double[]? JustifyOpen(double[] pattern, double lengthDu)
    {
        double p = SumAbs(pattern);
        if (p <= 0 || lengthDu <= 0)
            return null;
        int n = (int)Math.Floor((lengthDu + EpsDu) / p);
        if (n <= 0)
            return null;
        double rho = lengthDu - n * p;
        double half = pattern[0] / 2 + rho / 2;

        var result = new List<double>(pattern.Length * n + 2) { half };
        for (int i = 1; i < pattern.Length; i++)
            result.Add(pattern[i]);
        for (int rep = 0; rep < n - 1; rep++)
            result.AddRange(pattern);
        result.Add(half);
        return result.ToArray();
    }

    /// <summary>
    /// 閉じた曲線の端部整列。パターン全要素を一様倍率で伸縮し、周長ちょうど n 周期に合わせる。
    /// 戻り値は伸縮後の1周期ぶんの signed 長さ列（SVG の stroke-dasharray は周期的に繰り返すため
    /// 1周期分の記述で足りる）。周長が半周期未満で n=0 になる場合は null（実線）。
    /// </summary>
    public static double[]? JustifyClosed(double[] pattern, double perimeterDu)
    {
        double p = SumAbs(pattern);
        if (p <= 0 || perimeterDu <= 0)
            return null;
        // 最近接整数（四捨五入）。ちょうど n+0.5 になる同点ケースは実測が無いため、
        // .NET既定（銀行丸め）に委ねる（実務図面での厳密一致は稀有と判断）。
        int n = (int)Math.Round(perimeterDu / p, MidpointRounding.AwayFromZero);
        if (n <= 0)
            return null;
        double k = perimeterDu / (n * p);
        var result = new double[pattern.Length];
        for (int i = 0; i < pattern.Length; i++)
            result[i] = pattern[i] * k;
        return result;
    }

    /// <summary>
    /// 開いた曲線の端部整列後、周期内オフセット withinPeriodOffsetDu（パターン先頭=e[0]の始点
    /// からその記号までの累積距離。既存の ResolveLinetypeSymbols が算出する StartDu と同じ値を
    /// そのまま渡せる）に付随する記号の出現距離（曲線距離、区間 [0,segLenDu] にクランプ済み）を
    /// 列挙する。
    /// e[0]（両端で分割される実線）自身に記号が付く場合（TRACKS 等、実測で確認済み）も
    /// 同じ式で正しい: withinPeriodOffsetDu=|e[0]| のとき shift+withinOffset=half となり、
    /// 記号出現位置は「分割実線の半端片の終端」に一致する（自作 JP15SYM 線種の実測で
    /// L=15/20/26/37/45/60/100 の7ケース全てで ±0.02mm の代数一致を確認。
    /// tests/tools/cad-typography/linetype_symbol_check/justify_derive/sym0/ 参照）。
    /// </summary>
    public static IEnumerable<double> SymbolOccurrences(double[] pattern, double segLenDu,
                                                        double withinPeriodOffsetDu)
    {
        if (pattern.Length == 0)
            yield break;
        double p = SumAbs(pattern);
        if (p <= 0 || segLenDu <= 0)
            yield break;
        int n = (int)Math.Floor((segLenDu + EpsDu) / p);
        if (n <= 0)
            yield break;
        double rho = segLenDu - n * p;
        double half = pattern[0] / 2 + rho / 2;
        // シフト定数: 分割実線 e[0] の「両端片へ均等配分」を、周期内オフセットへ一様に反映する
        // （半端片 - 名目e[0] = 整列による正味のずれ）。
        double shift = half - pattern[0];

        for (int period = 0; period < n; period++)
        {
            double d = shift + period * p + withinPeriodOffsetDu;
            if (d < -EpsDu || d > segLenDu + EpsDu)
                continue;
            yield return Math.Min(Math.Max(d, 0), segLenDu);
        }
    }

    /// <summary>
    /// 閉じた曲線の端部整列後、周期内オフセット withinPeriodOffsetDu（e[0]の始点からの累積距離、
    /// JustifyOpen 系と同じ意味）に付随する記号の出現距離を列挙する。閉じた曲線は位相シフトが
    /// 無く（JustifyClosed 参照）、伸縮後の周期ごとに同じオフセットで繰り返すだけでよい。
    /// </summary>
    public static IEnumerable<double> SymbolOccurrencesClosed(double[] pattern, double perimeterDu,
                                                              double withinPeriodOffsetDu)
    {
        double p = SumAbs(pattern);
        if (p <= 0 || perimeterDu <= 0)
            yield break;
        int n = (int)Math.Round(perimeterDu / p, MidpointRounding.AwayFromZero);
        if (n <= 0)
            yield break;
        double k = perimeterDu / (n * p);
        double scaledPeriod = p * k;
        double startPos = withinPeriodOffsetDu * k;
        for (int period = 0; period < n; period++)
        {
            double d = startPos + period * scaledPeriod;
            if (d < -EpsDu || d > perimeterDu + EpsDu)
                continue;
            yield return Math.Min(Math.Max(d, 0), perimeterDu);
        }
    }

    static double SumAbs(double[] pattern)
    {
        double sum = 0;
        foreach (var e in pattern)
            sum += Math.Abs(e);
        return sum;
    }
}
