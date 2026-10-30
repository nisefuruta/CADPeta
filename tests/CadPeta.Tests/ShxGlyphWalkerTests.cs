// SHX バイトコード解釈（ShxGlyphWalker、命令0-14）の単体テスト。
// 合成バイト列で各命令を単独検証する（実物フォントでの統合検証は
// ShxFontIntegrationTests.cs）。ShxGlyphWalker は internal のため、
// CadPeta.Glyphs.Shx.csproj の InternalsVisibleTo でテストへ公開している。
using System;
using System.Linq;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class ShxGlyphWalkerTests
{
    static ShxGlyphWalker NewWalker(ShxFontKind kind = ShxFontKind.Unifont, bool isExtended = false) =>
        new(kind, isExtended, _ => null);

    [Fact]
    public void ベクトル命令は方向と長さのとおりに移動する()
    {
        // code1(ペンダウン) → vector(方向0=東,長さ5) = 0x50
        byte[] bc = { 1, 0x50 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(5, endX, 9);
        Assert.Equal(0, endY, 9);
        Assert.Equal(2, segs.Count);
        Assert.IsType<IrMove>(segs[0]);
        var line = Assert.IsType<IrLine>(segs[1]);
        Assert.Equal(5, line.X, 9);
        Assert.Equal(0, line.Y, 9);
    }

    [Fact]
    public void ベクトル命令の斜め方向は主軸成分を半分にする()
    {
        // 方向1=(1,0.5)、長さ4 → (4,2)。cb=(4<<4)|1=0x41
        AssertWalkEnd(new byte[] { 1, 0x41 }, 4, 2);
    }

    /// <summary>バイトコードを解釈した終端のペン位置が (<paramref name="endX"/>, <paramref name="endY"/>) になる。</summary>
    static void AssertWalkEnd(byte[] bytecode, double endX, double endY)
    {
        var (_, actualX, actualY) = NewWalker().Walk(bytecode);
        Assert.Equal(endX, actualX, 9);
        Assert.Equal(endY, actualY, 9);
    }

    [Fact]
    public void code4のスケール乗算を反映する()
    {
        // scale *= 2 → その後の vector(方向0,長さ5) は (10,0)
        AssertWalkEnd(new byte[] { 4, 2, 1, 0x50 }, 10, 0);
    }

    [Fact]
    public void code3のスケール除算を反映する()
    {
        // scale /= 2 → その後の vector(方向0,長さ5) は (2.5,0)
        AssertWalkEnd(new byte[] { 3, 2, 1, 0x50 }, 2.5, 0);
    }

    [Fact]
    public void pushしたペン位置をpopで復元し新しい部分パスを開始する()
    {
        // ペンダウン@(0,0) → (5,0) → push → (10,0) → pop で(5,0)へ復元
        byte[] bc = { 1, 0x50, 5, 0x50, 6 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(5, endX, 9);
        Assert.Equal(0, endY, 9);
        Assert.Equal(4, segs.Count); // Move(0,0),Line(5,0),Line(10,0),Move(5,0)
        var last = Assert.IsType<IrMove>(segs[3]); // pop後はIrLineでなくIrMoveで新しい部分パスになる
        Assert.Equal(5, last.X, 9);
        Assert.Equal(0, last.Y, 9);
    }

    [Fact]
    public void code8はXY変位1組を1回だけ適用する()
    {
        AssertWalkEnd(new byte[] { 1, 8, 3, 4 }, 3, 4);
    }

    [Fact]
    public void code9はXY変位列を00終端まで繰り返す()
    {
        // (3,4) → (-3,-3)(累積0,1) → (0,0)で終端
        byte[] bc = { 1, 9, 3, 4, 253, 253, 0, 0 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(0, endX, 9);
        Assert.Equal(1, endY, 9);
        Assert.Equal(3, segs.Count); // Move(0,0),Line(3,4),Line(0,1)
    }

    [Fact]
    public void code0はグリフを終端せず部分パスを区切るだけ()
    {
        // ペンダウン@(0,0) → (5,0) → code0(区切り) → (10,0)は新しい部分パス(IrMove)として続く
        byte[] bc = { 1, 0x50, 0, 0x50 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(10, endX, 9); // code0の後もバイト列の解釈が続いている証拠
        Assert.Equal(0, endY, 9);
        Assert.Equal(3, segs.Count); // Move(0,0),Line(5,0),Move(10,0)  ※code0以降がMoveで再開する
        Assert.IsType<IrMove>(segs[0]);
        Assert.IsType<IrLine>(segs[1]);
        Assert.IsType<IrMove>(segs[2]);
    }

    [Fact]
    public void code14は次の1命令を読み飛ばす()
    {
        // ペンダウン@(0,0) → code14 → vector(0x50、本来なら(5,0)へ移動)は読み飛ばされる
        // → vector(方向2=(1,1),長さ2=0x22)だけが実行され(2,2)に達する
        byte[] bc = { 1, 14, 0x50, 0x22 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(2, endX, 9);
        Assert.Equal(2, endY, 9);
        Assert.Equal(2, segs.Count); // Move(0,0),Line(2,2) のみ（読み飛ばされた分は増えない）
    }

    [Fact]
    public void 符号付きバイトはtwosComplement規則で変換される()
    {
        // 128→-128（下限）、127→127（上限、符号ビットなし）
        byte[] bc = { 1, 8, 128, 127 };
        var (_, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(-128, endX, 9);
        Assert.Equal(127, endY, 9);
    }

    [Fact]
    public void バルジ127はほぼ半円になる()
    {
        // (0,0)→(10,0)、bulge=127(=1.0)は半円(内角180°)。中心(5,0)・半径5で、
        // 反時計回り(正バルジ)の掃引はY負側を通る。区間角90°ごとに2分割されるため、
        // 分割境界の点は解析的に(5,-5)と厳密一致する。
        byte[] bc = { 1, 12, 10, 0, 127 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(10, endX, 9);
        Assert.Equal(0, endY, 9);
        var cubics = segs.OfType<IrCubic>().ToList();
        Assert.Equal(2, cubics.Count);
        Assert.Equal(5, cubics[0].X, 9);
        Assert.Equal(-5, cubics[0].Y, 9);

        // 弧全域が中心(5,0)・半径5の円上に乗ること（ベジェ近似誤差の許容0.01）
        const double cx = 5, cy = 0, r = 5;
        for (double t = 0; t <= 1.0001; t += 0.1)
        {
            var p = CubicChain.Eval(segs, Math.Min(t, 1));
            double d = Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy));
            Assert.True(Math.Abs(d - r) < 0.01,
                $"t={t:F1} で円から {Math.Abs(d - r):F4} ずれた");
        }
    }

    [Fact]
    public void バルジ0は直線になる()
    {
        byte[] bc = { 1, 12, 10, 0, 0 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        Assert.Equal(10, endX, 9);
        Assert.Equal(0, endY, 9);
        Assert.DoesNotContain(segs, s => s is IrCubic);
        var line = Assert.IsType<IrLine>(segs[1]);
        Assert.Equal(10, line.X, 9);
        Assert.Equal(0, line.Y, 9);
    }

    [Fact]
    public void オクタント弧は1オクタント分の端点に達する()
    {
        // 半径5、開始オクタント0、オクタント数1、反時計回り(flag=0x01)。
        // 中心=(0,0)-5*(cos0,sin0)=(-5,0)。終点=中心+5*(cos45,sin45)。
        byte[] bc = { 1, 10, 5, 0x01 };
        var (segs, endX, endY) = NewWalker().Walk(bc);
        double expected = 5 * Math.Sqrt(2) / 2.0;
        Assert.Equal(-5 + expected, endX, 9);
        Assert.Equal(expected, endY, 9);
        Assert.Single(segs.OfType<IrCubic>()); // 45°は90°以下のため1片のベジェで済む
    }

    [Fact]
    public void オクタント弧の時計回りフラグは逆方向へ掃引する()
    {
        // 半径5、開始オクタント0、オクタント数1、時計回り(flag=0x81=bit7セット)。
        byte[] bc = { 1, 10, 5, 0x81 };
        var (_, endX, endY) = NewWalker().Walk(bc);
        double expected = 5 * Math.Sqrt(2) / 2.0;
        Assert.Equal(-5 + expected, endX, 9);
        Assert.Equal(-expected, endY, 9); // Y符号が反時計回りの場合と逆になる
    }
}
