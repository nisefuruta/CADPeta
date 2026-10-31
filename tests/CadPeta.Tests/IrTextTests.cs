// IrText の幅係数の判定（EMF・PNG の描画が幅係数の変換を足すか）の試験。
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class IrTextTests
{
    // 標準とみなす帯（IrText.StandardWidthFactorTolerance = 0.001）の内と外の両側を確かめる
    [Theory]
    [InlineData(1.0, true)]
    [InlineData(1.0009, true)]
    [InlineData(0.9991, true)]
    [InlineData(1.0011, false)]
    [InlineData(0.9989, false)]
    [InlineData(0.8, false)]
    // 壊れた PDF の水平スケーリングから来うる NaN は、幅係数の変換を足さない（等倍として描く）
    [InlineData(double.NaN, true)]
    public void 幅係数は標準との差が帯の内なら標準とみなす(double widthFactor, bool expected)
    {
        var text = new IrText { WidthFactor = widthFactor };

        Assert.Equal(expected, text.HasStandardWidth);
    }
}
