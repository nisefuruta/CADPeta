using System.IO;
using System.Linq;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// -PETAOPT（設定のコマンドライン版）。今の設定を表示し、項目のキーワードで値を変え、Enter か終了(X)で保存する。
// 使えない値は理由を出して元の値のまま、Esc は変更を破棄して保存しない。変更が無ければファイルを作らない
public sealed class SettingsCommandLineTests
{
    sealed class Run : System.IDisposable
    {
        public readonly TempFolder Temp = new();
        public readonly FakeEditor Editor = new();
        public readonly StringWriter Log = new();
        public SettingsHolder Holder => new(SettingsPath);
        public string SettingsPath => Temp.File("settings.json");

        public Run(params PromptResult<string>[] keywords)
        {
            foreach (var k in keywords) Editor.KeywordAnswers.Enqueue(k);
        }

        public void Execute(SettingsHolder? holder = null) => SettingsCommandLine.Run(Editor, Log, holder ?? Holder);

        public void Dispose() => Temp.Dispose();

        public CadPetaSettings Saved => CadPetaSettingsStore.Load(SettingsPath, System.DateTime.Now).Settings;
    }

    static PromptResult<string> K(string global) => PromptResult<string>.OfKeyword(global);
    static readonly PromptResult<string> Enter = PromptResult<string>.OfNone();
    static readonly PromptResult<string> Esc = PromptResult<string>.OfCancelled();

    [Fact]
    public void 今の設定を表示する()
    {
        using var run = new Run(Enter);

        run.Execute();

        string text = string.Join("\n", run.Editor.Lines);
        Assert.Contains("EMF", text);
        Assert.Contains(CadPetaSettings.DefaultOutputFolder, text);
        Assert.Contains(FileNameTemplate.Default.Text, text);
        Assert.Contains(run.SettingsPath, text);
    }

    [Fact]
    public void 変更が無ければ保存しない()
    {
        using var run = new Run(Enter);

        run.Execute();

        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void 形式はカンマ区切りの一覧で入力した形式だけにして保存する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFormats), Enter);
        run.Editor.StringResponse = "pdf,PNG";

        run.Execute();

        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Pdf, ArtifactKind.Png), run.Saved.Formats);
    }

    [Fact]
    public void 形式は値の合計でも入力でき今の値は名前と値で表示する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFormats), Enter);
        run.Editor.StringResponse = "5";

        run.Execute();

        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Pdf, ArtifactKind.Emf), run.Saved.Formats);
        Assert.Contains(run.Editor.Lines, l => l.Contains("EMF (4)"));
    }

    [Fact]
    public void 形式のNONEはファイルを出さない設定にして保存する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFormats), Enter);
        run.Editor.StringResponse = "NONE";

        run.Execute();

        Assert.True(run.Saved.Formats.IsEmpty);
    }

    [Fact]
    public void 使えない形式の一覧は理由を出して元の値のまま()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFormats), Enter);
        run.Editor.StringResponse = "pdf,bmp";

        run.Execute();

        Assert.Contains(run.Editor.Lines, l => l.Contains(ArtifactKinds.ParseList("pdf,bmp").Error!));
        Assert.False(File.Exists(run.SettingsPath));
    }
    [Fact]
    public void 出力先を変えて保存する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFolder), Enter);
        run.Editor.StringResponse = @"D:\出力";

        run.Execute();

        Assert.Equal(@"D:\出力", run.Saved.OutputFolder);
    }

    [Fact]
    public void 使えない出力先は理由を出して元の値のまま()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFolder), Enter);
        run.Editor.StringResponse = "a|b";

        run.Execute();

        Assert.Contains(run.Editor.Lines, l => l.Contains(CadPetaSettings.ValidateOutputFolder("a|b")!));
        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void ファイル名の形を変えて保存する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordName), Enter);
        run.Editor.StringResponse = "{drawing}_{seq}";

        run.Execute();

        Assert.Equal("{drawing}_{seq}", run.Saved.FileName.Text);
    }

    [Fact]
    public void 使えないファイル名の形は理由を出して元の値のまま()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordName), Enter);
        run.Editor.StringResponse = "{bad}";

        run.Execute();

        Assert.Contains(run.Editor.Lines, l => l.Contains(FileNameTemplate.Parse("{bad}").Error));
        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void 項目の入力のEnterは元の値のまま()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFolder), Enter);
        run.Editor.StringResponse = "";

        run.Execute();

        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void PNGの解像度を変えて保存する()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordDpi), Enter);
        run.Editor.Doubles.Enqueue(PromptResult<double>.OfValue(150));

        run.Execute();

        Assert.Equal(150, run.Saved.PngDpi);
    }

    [Fact]
    public void 範囲の外のPNGの解像度は理由を出して元の値のまま()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordDpi), Enter);
        run.Editor.Doubles.Enqueue(PromptResult<double>.OfValue(5000));

        run.Execute();

        Assert.Contains(run.Editor.Lines, l => l.Contains(CadPetaSettings.ValidatePngDpi(5000)!));
        Assert.False(File.Exists(run.SettingsPath));
    }

    [Fact]
    public void Escは変更を破棄して保存しない()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordFormats), Esc);
        run.Editor.StringResponse = "pdf";

        run.Execute();

        Assert.False(File.Exists(run.SettingsPath));
        Assert.Contains(Msg.Text("opt.discarded"), run.Log.ToString());
    }

    [Fact]
    public void 保存した設定は同じ設定の持ち主の次の取得に使われる()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordDpi), Enter);
        run.Editor.Doubles.Enqueue(PromptResult<double>.OfValue(150));
        var holder = run.Holder;

        run.Execute(holder);

        Assert.Equal(150, holder.Get().Settings.PngDpi);
    }

    [Fact]
    public void 保存できなければ理由を出す()
    {
        using var run = new Run(K(SettingsCommandLine.KeywordDpi), Enter);
        run.Editor.Doubles.Enqueue(PromptResult<double>.OfValue(150));
        File.WriteAllText(run.SettingsPath, "{\"schemaVersion\": 1}");
        File.SetAttributes(run.SettingsPath, FileAttributes.ReadOnly);
        try
        {
            run.Execute();

            Assert.Contains(run.SettingsPath, run.Log.ToString());
            Assert.Equal(CadPetaSettings.DefaultPngDpi, run.Saved.PngDpi);
        }
        finally { File.SetAttributes(run.SettingsPath, FileAttributes.Normal); }
    }
}
