// CP932（Shift_JIS）変換の単体テスト。期待値は JIS X 0201/0208 の周知の対応
// （半角カナ0xA1起点、'亜'=JIS第1水準の先頭=0x889F 等）を直接の正解として使う。
using CadPeta.Glyphs.Shx;
using Xunit;

namespace CadPeta.Tests;

public class Cp932Tests
{
    [Fact]
    public void ASCII文字は恒等写像で返す()
    {
        Assert.True(Cp932.TryGetCode('A', out ushort code));
        Assert.Equal((ushort)0x41, code);
    }

    [Fact]
    public void 半角カナは1バイト符号を返す()
    {
        // 'ｱ'（U+FF71、半角カナのア）= JIS X 0201 の 0xB1
        Assert.True(Cp932.TryGetCode('ｱ', out ushort code));
        Assert.Equal((ushort)0xB1, code);
    }

    [Fact]
    public void 全角漢字は2バイト符号を返す()
    {
        // '亜'（U+4E9C）= JIS第1水準漢字の先頭 = CP932 0x889F
        Assert.True(Cp932.TryGetCode('亜', out ushort code));
        Assert.Equal((ushort)0x889F, code);
    }

    [Fact]
    public void 未収載文字はfalseを返す()
    {
        // U+FFFD（置換文字）は CP932 に対応する実体を持たない
        Assert.False(Cp932.TryGetCode('�', out ushort code));
        Assert.Equal((ushort)0, code);
    }
}
