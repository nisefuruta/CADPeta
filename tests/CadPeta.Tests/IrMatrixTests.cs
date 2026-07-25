using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

// IrMatrix.TryInverse は構造SVG書き出しのグラデーションの document→local 逆写像と
// 4アダプタの回転画像配置が共有する単一実装。往復と特異拒否を固定する。
public class IrMatrixTests
{
    [Fact]
    public void TryInverseは正則行列で往復して単位行列へ戻る()
    {
        var m = new IrMatrix(2, .5, -.3, 1.4, 12, -7); // 回転・スケール・平行移動を含む
        Assert.True(m.TryInverse(out IrMatrix inv));
        IrMatrix roundTrip = m.Multiply(inv);
        Assert.Equal(1, roundTrip.A, 12);
        Assert.Equal(0, roundTrip.B, 12);
        Assert.Equal(0, roundTrip.C, 12);
        Assert.Equal(1, roundTrip.D, 12);
        Assert.Equal(0, roundTrip.E, 12);
        Assert.Equal(0, roundTrip.F, 12);
    }

    [Fact]
    public void TryInverseは平行移動を打ち消して原点写像を戻す()
    {
        var m = new IrMatrix(1, 0, 0, 1, 30, -20);
        Assert.True(m.TryInverse(out IrMatrix inv));
        var (x, y) = inv.Apply(30, -20);
        Assert.Equal(0, x, 12);
        Assert.Equal(0, y, 12);
    }

    [Fact]
    public void TryInverseは特異行列と非有限成分を拒否する()
    {
        Assert.False(new IrMatrix(1, 0, 2, 0, 0, 0).TryInverse(out _)); // det=0
        Assert.False(new IrMatrix(double.NaN, 0, 0, 1, 0, 0).TryInverse(out _));
        Assert.False(new IrMatrix(1, 1, 1, 1 + 1e-14, 0, 0).TryInverse(out _));
        Assert.False(new IrMatrix(1, 0, 0, 1, double.PositiveInfinity, 0).TryInverse(out _));
    }

    [Fact]
    public void TryInverseは一様な極小尺度を絶対値で特異扱いしない()
    {
        const double tinyUniformScale = 1e-20;
        var matrix = new IrMatrix(tinyUniformScale, 0, 0, tinyUniformScale, 0, 0);
        Assert.True(matrix.TryInverse(out IrMatrix inverse));
        IrMatrix roundTrip = matrix.Multiply(inverse);
        Assert.Equal(1, roundTrip.A, 12);
        Assert.Equal(1, roundTrip.D, 12);
    }
}
