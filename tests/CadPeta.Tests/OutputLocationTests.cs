using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA の出力先は設定の出力先。相対パスは図面のフォルダが起点で、書き込めれば聞かずに使う。
// 起点が無い（保存していない図面）か、書き込めないときだけ、理由を出して出力先を聞く。
// フォルダは成果物を写すときに作る（書き込めるかを確かめるために作ったフォルダは残さない）
public sealed class OutputLocationTests
{
    static CadPetaSettings Folder(string folder) => CadPetaSettings.Default with { OutputFolder = folder };

    [Fact]
    public void 相対パスは図面のフォルダを起点に聞かずに使う()
    {
        using var temp = new TempFolder();
        var ed = new FakeEditor { DrawingPath = Path.Combine(temp.Path, "a.dwg") };

        string dir = OutputLocation.ResolveFor(ed, TextWriter.Null, Folder(@".\CADPeta"));

        Assert.Equal(Path.Combine(temp.Path, "CADPeta"), dir);
        Assert.Empty(ed.StringMessages);
    }

    [Fact]
    public void 書き込めるかを確かめるために作ったフォルダは残さない()
    {
        using var temp = new TempFolder();
        var ed = new FakeEditor { DrawingPath = Path.Combine(temp.Path, "a.dwg") };

        OutputLocation.ResolveFor(ed, TextWriter.Null, Folder(@".\out\sub"));

        Assert.False(Directory.Exists(Path.Combine(temp.Path, "out")));
    }

    [Fact]
    public void あったフォルダとその中のファイルはそのまま残す()
    {
        using var temp = new TempFolder();
        string dir = Path.Combine(temp.Path, "out");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.emf"), "");
        var ed = new FakeEditor { DrawingPath = Path.Combine(temp.Path, "a.dwg") };

        OutputLocation.ResolveFor(ed, TextWriter.Null, Folder(@".\out"));

        Assert.Equal(new[] { Path.Combine(dir, "x.emf") }, Directory.GetFiles(dir));
    }

    [Fact]
    public void 完全なパスは保存していない図面でも聞かずに使う()
    {
        using var temp = new TempFolder();
        var ed = new FakeEditor { IsDrawingSaved = false, DrawingPath = "" };
        string folder = Path.Combine(temp.Path, "出力");

        Assert.Equal(folder, OutputLocation.ResolveFor(ed, TextWriter.Null, Folder(folder)));
        Assert.Empty(ed.StringMessages);
    }

    [Fact]
    public void 保存していない図面で相対パスなら理由を出して出力先を聞く()
    {
        using var temp = new TempFolder();
        var ed = new FakeEditor { IsDrawingSaved = false, DrawingPath = "", StringResponse = temp.Path };
        var log = new StringWriter();

        Assert.Equal(temp.Path, OutputLocation.ResolveFor(ed, log, Folder(@".\CADPeta")));
        Assert.Single(ed.StringMessages);
        Assert.Contains(Msg.Text("output.unsaved"), log.ToString());
    }

    [Fact]
    public void 出力先を作れなければ理由と例外の文を出して出力先を聞く()
    {
        using var temp = new TempFolder();
        string file = Path.Combine(temp.Path, "file");
        File.WriteAllText(file, "");
        string bad = Path.Combine(file, "sub");
        var ed = new FakeEditor { StringResponse = temp.Path };
        var log = new StringWriter();

        // 理由は、そのフォルダを作ろうとしたときの例外の文（OS の言語で変わるので、同じ操作で得た文と比べる）
        string reason = "";
        try { Directory.CreateDirectory(bad); } catch (IOException ex) { reason = FileFailures.OneLine(ex.Message); }

        Assert.Equal(temp.Path, OutputLocation.ResolveFor(ed, log, Folder(bad)));
        Assert.Contains(bad, log.ToString());
        Assert.NotEqual("", reason);
        Assert.Contains(reason, log.ToString());
    }
}