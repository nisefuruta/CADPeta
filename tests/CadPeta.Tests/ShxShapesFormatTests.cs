// SHX shapes 形式（AutoCAD-86 shapes 1.0）の単体テスト。
// バイト配置は本機実物 ltypeshp.shx（複雑線種の記号シェイプ集）の実測で確定した構造
// （先頭シェイプ番号・末尾シェイプ番号・エントリ数のヘッダ→(code,length)索引→
//  (name,NUL,bytecode)の本体連続配置。shape0(code=0)は任意）に従い、合成バイト列で検証する。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using Xunit;

namespace CadPeta.Tests;

public class ShxShapesFormatTests
{
    /// <summary>ヘッダ＋(first,last,count)＋索引＋本体、という shapes 形式の合成ファイルを組み立てる。
    /// shape0Above が null なら shape0（code=0）を含めない（本機実物 ltypeshp.shx と同型）。</summary>
    static byte[] BuildShapesFile(
        string version,
        IReadOnlyList<(int Code, string Name, byte[] Bytecode)> shapes,
        int? shape0Above = null, int shape0Below = 0, int shape0Modes = 0)
    {
        var body = new List<byte>();
        void U16(int v) { body.Add((byte)(v & 0xFF)); body.Add((byte)((v >> 8) & 0xFF)); }

        var allShapes = new List<(int Code, byte[] Raw)>();
        if (shape0Above != null)
        {
            var shape0Body = new List<byte>();
            shape0Body.AddRange(Encoding.ASCII.GetBytes("FONTDESC"));
            shape0Body.Add(0);
            shape0Body.Add((byte)shape0Above.Value);
            shape0Body.Add((byte)shape0Below);
            shape0Body.Add((byte)shape0Modes);
            allShapes.Add((0, shape0Body.ToArray()));
        }
        foreach (var (code, name, bytecode) in shapes)
        {
            var raw = new List<byte>();
            raw.AddRange(Encoding.ASCII.GetBytes(name));
            raw.Add(0);
            raw.AddRange(bytecode);
            allShapes.Add((code, raw.ToArray()));
        }

        int first = allShapes.Count > 0 ? allShapes.Min(s => s.Code) : 0;
        int last = allShapes.Count > 0 ? allShapes.Max(s => s.Code) : 0;
        U16(first);
        U16(last);
        U16(allShapes.Count);
        foreach (var (code, raw) in allShapes)
        {
            U16(code);
            U16(raw.Length);
        }
        foreach (var (_, raw) in allShapes)
            body.AddRange(raw);

        var header = Encoding.ASCII.GetBytes($"AutoCAD-86 shapes {version}");
        var full = new List<byte>();
        full.AddRange(header);
        full.Add(0x0D); full.Add(0x0A); full.Add(0x1A);
        full.AddRange(body);
        return full.ToArray();
    }

    [Fact]
    public void shape0が無いファイルはAbove0でHasShape0falseになる()
    {
        // 本機実物 ltypeshp.shx と同型（複雑線種の記号シェイプ集はテキストフォントでないため
        // above 基準を持たない）。
        var data = BuildShapesFile("1.0", new (int, string, byte[])[]
        {
            (130, "FOO", new byte[] { 1, 0x50 }), // ペンダウン→vector(方向0,長さ5)
        });

        var font = ShxFontFile.Parse(data, "test");
        Assert.Equal(ShxFontKind.Shapes, font.Kind);
        Assert.False(font.HasShape0);
        Assert.Equal(0, font.Above);
        Assert.False(font.IsExtended);
        Assert.Single(font.Glyphs);
        Assert.True(font.Glyphs.ContainsKey(130));
        Assert.Equal(130, font.ShapeNames["FOO"]);
    }

    [Fact]
    public void シェイプ番号による取得と名前から番号への解決ができる()
    {
        var data = BuildShapesFile("1.0", new (int, string, byte[])[]
        {
            (130, "TRACK1", new byte[] { 1, 0x50 }),
            (131, "ZIG", new byte[] { 0x50 }),
        });

        var font = ShxFontFile.Parse(data, "test");
        Assert.Equal(2, font.Glyphs.Count);
        Assert.Equal(2, font.ShapeNames.Count);

        // 名前→番号の解決
        Assert.Equal(130, font.ShapeNames["TRACK1"]);
        Assert.Equal(131, font.ShapeNames["ZIG"]);
        // 大文字小文字を区別しない解決（利用者が線種定義から引く際の便宜。実物は常に大文字）
        Assert.Equal(131, font.ShapeNames["zig"]);

        // 番号による取得（Glyphs 経由）
        Assert.Equal(new byte[] { 1, 0x50 }, font.Glyphs[130]);
        Assert.Equal(new byte[] { 0x50 }, font.Glyphs[131]);
    }

    [Fact]
    public void code7のサブシェイプ呼出は1バイトのシェイプ番号でペン状態を引き継ぐ()
    {
        // BAR: ペンダウン→code7でFOO(番号130)を呼出。FOOはvector(方向0,長さ5)のみで
        // 自身はペンダウンしない（実物のTRACK1/ZIG/BOX等と同型で、呼出元のペン状態を引き継ぐ前提）。
        var data = BuildShapesFile("1.0", new (int, string, byte[])[]
        {
            (130, "FOO", new byte[] { 0x50 }),         // vector(方向0,長さ5) のみ
            (131, "BAR", new byte[] { 1, 7, 130 }),    // ペンダウン→code7(shapes:1バイト番号)→FOO
        });

        var font = ShxFontFile.Parse(data, "test");
        var walker = new ShxGlyphWalker(font.Kind, font.IsExtended,
            code => font.Glyphs.TryGetValue(code, out var b) ? b : null);
        var (segs, endX, endY) = walker.Walk(font.Glyphs[131]);

        Assert.Equal(5, endX, 9);
        Assert.Equal(0, endY, 9);
        Assert.Equal(2, segs.Count);
        var move = Assert.IsType<IrMove>(segs[0]);
        Assert.Equal(0, move.X, 9); Assert.Equal(0, move.Y, 9);
        var line = Assert.IsType<IrLine>(segs[1]);
        Assert.Equal(5, line.X, 9); Assert.Equal(0, line.Y, 9);
    }

    [Fact]
    public void shape0があればaboveBelowModesを解決しHasShape0trueになる()
    {
        var data = BuildShapesFile("1.0",
            new (int, string, byte[])[] { (1, "A", new byte[] { 1, 0x50 }) },
            shape0Above: 21, shape0Below: 7, shape0Modes: 0);

        var font = ShxFontFile.Parse(data, "test");
        Assert.True(font.HasShape0);
        Assert.Equal(21, font.Above);
        Assert.Equal(7, font.Below);
        Assert.False(font.Glyphs.ContainsKey(0)); // shape0はフォント諸元でありGlyphsに含まない
    }

    [Fact]
    public void shape0があるのにaboveが0以下なら破損データとして拒否する()
    {
        var data = BuildShapesFile("1.0",
            new (int, string, byte[])[] { (1, "A", new byte[] { 1, 0x50 }) },
            shape0Above: 0, shape0Below: 0, shape0Modes: 0);

        var ex = Assert.Throws<InvalidDataException>(() => ShxFontFile.Parse(data, "test"));
        Assert.Contains("above", ex.Message);
    }

    [Fact]
    public void shapes11は実測が無いため明示的に拒否する()
    {
        var header = Encoding.ASCII.GetBytes("AutoCAD-86 shapes 1.1");
        var data = header.Concat(new byte[] { 0x0D, 0x0A, 0x1A }).ToArray();

        var ex = Assert.Throws<NotSupportedException>(() => ShxFontFile.Parse(data, "test"));
        Assert.Contains("1.1", ex.Message);
    }

    [Fact]
    public void above基準の無いフォントは文字輪郭化に使えず明示的に失敗する()
    {
        // shapes形式でshape0が無い(above=0)ファイルをテキストスタイルへ誤って指定した場合の
        // 誤読防止(ゼロ除算にならず、falseとfailReasonで明示的に失敗すること)を検証する。
        var data = BuildShapesFile("1.0", new (int, string, byte[])[]
        {
            (65, "A", new byte[] { 1, 0x50 }), // 'A'=0x41=65 として文字コードに直接使う
        });

        using var temp = new TempFolder();
        string path = temp.File("noshape0.shx");
        File.WriteAllBytes(path, data);
        var outliner = new ShxGlyphOutliner();
        var run = CadPeta.UseCases.GlyphRun.Shx("A", path, null, capHeightMm: 3.0, widthFactor: 1.0, glyphXsMm: null);
        Assert.False(outliner.TryOutline(run, out var segs, out var fail));
        Assert.Null(segs);
        Assert.NotNull(fail);
        Assert.Contains("above", fail);
    }
}
