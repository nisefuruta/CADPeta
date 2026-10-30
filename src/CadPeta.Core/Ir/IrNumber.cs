using System.Globalization;

namespace CadPeta.Ir;

/// <summary>
/// IR の数値を出力ファイル(SVG・PDF)へ書く文字列。丸め結果の「-0」は「0」に畳む:
/// 負の極小値(Y 反転の三角関数誤差など)の文字列化が .NET Framework では「0」、.NET Core 系では「-0」に割れ、
/// 同じ図面の出力が実行基盤の間でバイト単位で一致しなくなる(実測)。
/// </summary>
public static class IrNumber
{
    /// <summary>座標・長さ(mm)。0.03pt(PDF のコンテンツの量子)= 0.0106mm なので、小数 4 桁(0.1µm)で量子を十分下回る。</summary>
    public static string Coordinate(double value) => Fold(value.ToString("0.####", CultureInfo.InvariantCulture));

    /// <summary>変換行列の成分(小数 6 桁)。回転の成分の丸め誤差は座標の大きさ倍で効くため、座標より細かく書く。</summary>
    public static string Matrix(double value) => Fold(value.ToString("0.######", CultureInfo.InvariantCulture));

    static string Fold(string formatted) => formatted == "-0" ? "0" : formatted;
}
