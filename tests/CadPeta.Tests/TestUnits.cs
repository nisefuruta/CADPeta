using System;

namespace CadPeta.Tests;

/// <summary>
/// 試験の期待値に使う mm とインチ・ピクセルの換算。試験される <c>Units</c> は使わず、定義(1 インチ = 25.4 mm)から独立に計算する。
/// <c>Units</c> を使うと、その誤りが期待値にも入り、試験が何も確かめなくなる。試験が mm とインチを換算するのはこの型だけにする
/// (TestCodeRulesTests が検査する)。
/// </summary>
internal static class TestUnits
{
    /// <summary>1 インチの長さ(mm)。</summary>
    public const double MmPerInch = 25.4;

    /// <summary>長さ <paramref name="mm"/> を解像度 <paramref name="dpi"/> で出力したときのピクセル数(四捨五入)。</summary>
    public static int PixelsAt(double mm, double dpi) => (int)Math.Round(mm * dpi / MmPerInch);
}
