// 複雑線種の記号（シェイプ・文字）展開の純粋計算部分の単体テスト。
// 角度解決の意味論（R=/A=/U=）は acadiso.lin 実物＋自作の実験線種を entget で実測して確定した
// （tests/tools/cad-typography/linetype_symbol_check、2026-07-10）。
using CadPeta.Acquire.Common;
using Xunit;

namespace CadPeta.Tests;

public class LinetypeSymbolMathTests
{
    // ---------- ResolveAngle（R=/A=/U= の回転解決） ----------

    [Fact]
    public void 既定Rは接線角と指定角の和になる()
    {
        double tangent = Math.PI / 4; // 45°
        double rot = Math.PI / 4;     // R=45°
        double result = LinetypeSymbolMath.ResolveAngle(tangent, rot, ucsOriented: false, upright: false);
        Assert.Equal(Math.PI / 2, result, 9);
    }

    [Fact]
    public void 絶対回転Aは接線角を無視し指定角のみになる()
    {
        double tangent = Math.PI; // 接線が真後ろを向いていても
        double rot = Math.PI / 6; // A=30°
        double result = LinetypeSymbolMath.ResolveAngle(tangent, rot, ucsOriented: true, upright: false);
        Assert.Equal(Math.PI / 6, result, 9);
    }

    [Fact]
    public void 正立Uは相対角が右向きなら反転しない()
    {
        double tangent = 0; // 右向き（+X）
        double rot = 0;
        double result = LinetypeSymbolMath.ResolveAngle(tangent, rot, ucsOriented: false, upright: true);
        Assert.Equal(0.0, result, 9);
    }

    [Fact]
    public void 正立Uは相対角が左向きなら180度反転して正立を保つ()
    {
        double tangent = Math.PI; // 左向き（-X）。そのままだと文字が逆さに見える
        double rot = 0;
        double result = LinetypeSymbolMath.ResolveAngle(tangent, rot, ucsOriented: false, upright: true);
        // π + π = 2π（= 0 と等価）。cos(2π)=1 なので反転後は右向き
        Assert.Equal(1.0, Math.Cos(result), 9);
    }

    [Fact]
    public void 正立Uは境界角90度では反転しない()
    {
        // cos(90°)=0 は「反転しない」側（Math.Cos(rel) < 0 の判定は厳密未満）
        double result = LinetypeSymbolMath.ResolveAngle(Math.PI / 2, 0, ucsOriented: false, upright: true);
        Assert.Equal(Math.PI / 2, result, 9);
    }

    // ---------- ShouldSubstituteDotLength（偽ドット回避） ----------

    [Fact]
    public void 記号を持たない長さ0要素はドットへ置換する()
    {
        Assert.True(LinetypeSymbolMath.ShouldSubstituteDotLength(0, hasSymbol: false));
    }

    [Fact]
    public void 記号を持つ長さ0要素はドットへ置換しない()
    {
        Assert.False(LinetypeSymbolMath.ShouldSubstituteDotLength(0, hasSymbol: true));
    }

    [Fact]
    public void 長さが0でない要素は記号の有無に関わらず置換しない()
    {
        Assert.False(LinetypeSymbolMath.ShouldSubstituteDotLength(1.5, hasSymbol: false));
        Assert.False(LinetypeSymbolMath.ShouldSubstituteDotLength(-1.5, hasSymbol: true));
    }
}
