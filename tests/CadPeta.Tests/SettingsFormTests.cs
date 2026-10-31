using System;
using CadPeta.Geometry;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETAOPT のダイアログの入力（表示モデル）。今の設定から始め、項目ごとに使えない値の理由を返し、すべて使えるときだけ設定にする。
// 検証は CadPetaSettings と FileNameTemplate と同じ（-PETAOPT、設定のファイルの読み込みと同じ判定）
public sealed class SettingsFormTests
{
    static readonly DateTime Now = new(2026, 10, 28, 18, 0, 0);

    [Fact]
    public void 今の設定の値から始まる()
    {
        var s = CadPetaSettings.Default with { OutputFolder = @"D:\out", Formats = ArtifactKindSet.Of(ArtifactKind.Pdf), PngDpi = 150 };

        var form = new SettingsForm(s);

        Assert.Equal(@"D:\out", form.OutputFolder);
        Assert.Equal(FileNameTemplate.Default.Text, form.FileName);
        Assert.True(form.IsOn(ArtifactKind.Pdf));
        Assert.False(form.IsOn(ArtifactKind.Emf));
        Assert.Equal("150", form.PngDpi);
        Assert.Equal(s, form.ToSettings());
    }

    [Fact]
    public void 変えた値で設定を作る()
    {
        var form = new SettingsForm(CadPetaSettings.Default);
        form.SetOn(ArtifactKind.Svg, true);
        form.SetOn(ArtifactKind.Emf, false);
        form.OutputFolder = @"..\出力";
        form.FileName = "{drawing}_{seq}";
        form.PngDpi = "200";

        var s = form.ToSettings();

        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Svg), s!.Formats);
        Assert.Equal(@"..\出力", s.OutputFolder);
        Assert.Equal("{drawing}_{seq}", s.FileName.Text);
        Assert.Equal(200, s.PngDpi);
    }

    [Fact]
    public void 使えない出力先は理由を返し設定にしない()
    {
        var form = new SettingsForm(CadPetaSettings.Default) { OutputFolder = "a|b" };

        Assert.Equal(CadPetaSettings.ValidateOutputFolder("a|b"), form.OutputFolderError);
        Assert.Null(form.ToSettings());
    }

    [Fact]
    public void 使えないファイル名の形は理由を返し設定にしない()
    {
        var form = new SettingsForm(CadPetaSettings.Default) { FileName = "{bad}" };

        Assert.Equal(FileNameTemplate.Parse("{bad}").Error, form.FileNameError);
        Assert.Null(form.ToSettings());
    }

    // 範囲の判定は CadPetaSettingsTests が確かめる。ここは入力の文字列から数への変換を通した結果だけを確かめる
    [Theory]
    [InlineData("24", true)]
    [InlineData("5000", false)]
    [InlineData("abc", false)]
    [InlineData("", false)]
    public void PNGの解像度の入力の文字列が使えるときだけ設定にする(string dpi, bool ok)
    {
        var form = new SettingsForm(CadPetaSettings.Default with { Formats = ArtifactKindSet.Of(ArtifactKind.Png) }) { PngDpi = dpi };

        Assert.Equal(ok, form.PngDpiError == null);
        Assert.Equal(ok, form.ToSettings() != null);
    }

    // PNG をオフにすると解像度の欄は使えなくなり、利用者は欄の値を直せない。不正な値のまま OK を押せなくしない
    [Fact]
    public void PNGがオフなら解像度の入力を検証せず元の解像度を保存する()
    {
        var form = new SettingsForm(CadPetaSettings.Default with { Formats = ArtifactKindSet.Of(ArtifactKind.Png), PngDpi = 150 }) { PngDpi = "abc" };
        form.SetOn(ArtifactKind.Png, false);

        Assert.Null(form.PngDpiError);
        Assert.Equal(150, form.ToSettings()!.PngDpi);
    }

    // フォルダを選ぶ画面の最初のフォルダ。.NET Framework の Path.IsPathRooted は使えない文字で例外を投げるので、検証を通った完全なパスだけを返す
    [Theory]
    [InlineData(@"D:\out", @"D:\out")]
    [InlineData(@"\\server\share", @"\\server\share")]
    [InlineData(@"..\出力", null)]
    [InlineData("a|b", null)]
    [InlineData("C:\\a\u0001", null)]
    public void フォルダを選ぶ画面の最初のフォルダは使える完全なパスだけ(string folder, string? expected)
    {
        var form = new SettingsForm(CadPetaSettings.Default) { OutputFolder = folder };

        Assert.Equal(expected, form.FullOutputFolder);
    }

    [Fact]
    public void ファイル名の例は形に例の値を差し込みEMFの拡張子を付ける()
    {
        var form = new SettingsForm(CadPetaSettings.Default) { FileName = "{drawing}_{seq}" };

        Assert.Equal("図面_001.emf", form.Example("図面", "Model", Now));
    }

    [Fact]
    public void 使えないファイル名の形では例を出さない()
    {
        var form = new SettingsForm(CadPetaSettings.Default) { FileName = "{bad}" };

        Assert.Equal("", form.Example("図面", "Model", Now));
    }

    [Fact]
    public void 値が変わると変わった項目を知らせる()
    {
        var form = new SettingsForm(CadPetaSettings.Default);
        var changed = new System.Collections.Generic.List<string?>();
        form.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        form.OutputFolder = "x";

        Assert.Contains(nameof(SettingsForm.OutputFolder), changed);
        Assert.Contains(nameof(SettingsForm.OutputFolderError), changed);
    }

    // PNG の解像度の文字の解釈（PETAOPT のダイアログと cadpeta-gui.exe の欄が共有する）。前後の空白は除く
    [Theory]
    [InlineData("150", 150)]
    [InlineData(" 300 ", 300)]
    public void PNGの解像度の文字を数にする(string text, double expected)
    {
        Assert.Equal(((string?)null, expected), (CadPetaSettings.ParsePngDpi(text, out double dpi), dpi));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("5000")]
    public void PNGの解像度が数でないか範囲の外なら理由を返す(string text)
    {
        Assert.NotNull(CadPetaSettings.ParsePngDpi(text, out _));
    }
}
