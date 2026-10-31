using System;
using System.IO;
using CadPeta.Geometry;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// CADPeta の設定（利用者ごとに 1 つの settings.json）。既定値・範囲・検証は CadPetaSettings の 1 か所で決め、
// PETA・PETAOPT・CLI・GUI・PNG の書き出しはすべてこれを使う。既定は EMF だけを .\CADPeta に出し、PNG は 300 DPI。
// 一部の値がおかしければその値だけを既定に直して保存し、JSON として読めなければ既定で作り直す。どちらも直す前のファイルを
// .bak-<日時> で残し、直した項目と残したファイルを返す（黙って既定に戻さない）
public class CadPetaSettingsTests
{
    static readonly DateTime Now = new(2026, 10, 28, 18, 0, 0);

    [Fact]
    public void 既定はEMFだけを図面のフォルダのCADPetaに300DPIで出す()
    {
        var s = CadPetaSettings.Default;

        Assert.Equal(@".\CADPeta", s.OutputFolder);
        Assert.Equal(FileNameTemplate.Default, s.FileName);
        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Emf), s.Formats);
        Assert.Equal(300, s.PngDpi);
    }

    [Theory]
    [InlineData(RasterLimits.MinDpi, true)]
    [InlineData(CadPetaSettings.MaxPngDpi, true)]
    [InlineData(RasterLimits.MinDpi - 1, false)]
    [InlineData(CadPetaSettings.MaxPngDpi + 1, false)]
    [InlineData(double.NaN, false)]
    [InlineData(double.PositiveInfinity, false)]
    public void PNGの解像度は範囲の中だけ受け付ける(double dpi, bool ok)
    {
        Assert.Equal(ok, CadPetaSettings.ValidatePngDpi(dpi) == null);
    }

    [Fact]
    public void PNGの解像度の上限ではA0の長い辺が画像の1辺の上限に収まる()
    {
        const double A0LongSideMm = 1189;

        Assert.True(A0LongSideMm / Units.MmPerInch * CadPetaSettings.MaxPngDpi <= RasterLimits.MaxDimensionPx);
    }

    [Fact]
    public void ファイルが無ければ既定を使いファイルは作らない()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "settings.json");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Missing, r.Status);
        Assert.Equal(CadPetaSettings.Default, r.Settings);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void 保存した設定を読み直すと同じ値()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "settings.json");
        var s = CadPetaSettings.Default with
        {
            OutputFolder = @"D:\出力\図",
            FileName = FileNameTemplate.Parse("{drawing}_{comment}_{seq}").Template!,
            Formats = ArtifactKindSet.Of(ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Png),
            PngDpi = 150,
        };

        Assert.Null(CadPetaSettingsStore.Save(path, s));
        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Ok, r.Status);
        Assert.Equal(s, r.Settings);
        Assert.Empty(r.Problems);
    }

    // 実機の試験のスクリプト（tests/tools/common/test_helpers.ps1 の Write-E2eSettings）が書く形。キーの名前を文字列のまま固定する
    [Fact]
    public void 試験のスクリプトが書く形の設定をそのまま読める()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json",
            "{\"schemaVersion\":1,\"outputFolder\":\".\",\"fileName\":\"{drawing}_{tab}_{datetime}\",\"formats\":[\"pdf\",\"svg\",\"emf\"],\"pngDpi\":300}");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Ok, r.Status);
        Assert.Equal(new CadPetaSettings(".", FileNameTemplate.Default,
            ArtifactKindSet.Of(ArtifactKind.Pdf, ArtifactKind.Svg, ArtifactKind.Emf), 300), r.Settings);
    }

    // 保存の失敗は例外ではなく理由で返す。使えない文字を含む場所は、.NET Framework では ArgumentException になる
    [Fact]
    public void 使えない文字を含む場所へは保存せず理由を返す()
    {
        using var temp = new TempFolder();
        string path = temp.Path + @"\a|b\settings.json"; // Path.Combine は .NET Framework で使えない文字を拒むので連結する

        Assert.NotNull(CadPetaSettingsStore.Save(path, CadPetaSettings.Default));
    }

    [Fact]
    public void 形式を1つも出さない設定も保存して読み直せる()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "settings.json");
        var s = CadPetaSettings.Default with { Formats = ArtifactKindSet.Empty };

        Assert.Null(CadPetaSettingsStore.Save(path, s));

        Assert.Equal(s, CadPetaSettingsStore.Load(path, Now).Settings);
    }

    [Theory]
    [InlineData("\"pngDpi\": 5000", "pngDpi")]
    [InlineData("\"pngDpi\": \"高い\"", "pngDpi")]
    [InlineData("\"fileName\": \"{drawing}_{unknown}\"", "fileName")]
    [InlineData("\"outputFolder\": \"a|b\"", "outputFolder")]
    [InlineData("\"outputFolder\": \"C:\\\\out:pdf\"", "outputFolder")]
    [InlineData("\"formats\": [\"emf\", \"bmp\"]", "formats")]
    [InlineData("\"formats\": \"emf\"", "formats")]
    [InlineData("\"color\": \"red\"", "color")]
    [InlineData("\"pngDpi\": 300, \"pngDpi\": 150", "pngDpi")]
    public void 一部の値がおかしければその値だけ既定に直して保存し直す前のファイルを残す(string badMember, string item)
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"outputFolder\": \"D:\\\\out\", " + badMember + "}");
        string original = File.ReadAllText(path);

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Repaired, r.Status);
        Assert.Contains(r.Problems, p => p.Item == item);
        Assert.StartsWith(path + ".bak-", r.BackupPath);
        Assert.Equal(original, File.ReadAllText(r.BackupPath!));
        Assert.Equal(r.Settings, CadPetaSettingsStore.Load(path, Now).Settings);
        Assert.Equal(SettingsLoadStatus.Ok, CadPetaSettingsStore.Load(path, Now).Status);
    }

    [Fact]
    public void 形式の一覧のうち知らない名前だけを除いて残りは使う()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"formats\": [\"svg\", \"bmp\", \"png\"]}");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(ArtifactKindSet.Of(ArtifactKind.Svg, ArtifactKind.Png), r.Settings.Formats);
    }

    [Fact]
    public void 書いていない項目は既定を使い直したことにしない()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"pngDpi\": 150}");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Ok, r.Status);
        Assert.Equal(150, r.Settings.PngDpi);
        Assert.Equal(CadPetaSettings.Default.Formats, r.Settings.Formats);
        Assert.Null(r.BackupPath);
    }

    [Theory]
    [InlineData("{ \"schemaVersion\": 1, ")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"schemaVersion\": 1, \"formats\": [ \"emf\" ")]
    [InlineData("{ \"fileName\": \"{drawing}\" ")]
    public void JSONとして読めなければ既定で作り直し読めなかったファイルを残す(string broken)
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", broken);

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Recreated, r.Status);
        Assert.Equal(CadPetaSettings.Default, r.Settings);
        Assert.StartsWith(path + ".bak-", r.BackupPath);
        Assert.Equal(broken, File.ReadAllText(r.BackupPath!));
        Assert.Equal(SettingsLoadStatus.Ok, CadPetaSettingsStore.Load(path, Now).Status);
    }

    [Fact]
    public void 新しい版の設定は書き換えずに既定を使う()
    {
        using var temp = new TempFolder();
        string json = "{\"schemaVersion\": 99, \"formats\": [\"png\"]}";
        string path = temp.WriteFile("settings.json", json);

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.NewerVersion, r.Status);
        Assert.Equal(CadPetaSettings.Default, r.Settings);
        Assert.Equal(json, File.ReadAllText(path));
        Assert.Null(r.BackupPath);
    }

    [Fact]
    public void 新しい版の設定には保存せずに理由を返す()
    {
        using var temp = new TempFolder();
        string json = "{\"schemaVersion\": 99, \"formats\": [\"png\"]}";
        string path = temp.WriteFile("settings.json", json);

        string? error = CadPetaSettingsStore.Save(path, CadPetaSettings.Default);

        Assert.NotNull(error);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void 直した設定を保存できなければ既定を使い場所と削除の案内を返す()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "not json");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Equal(SettingsLoadStatus.Unrepairable, r.Status);
        Assert.Equal(CadPetaSettings.Default, r.Settings);
        Assert.Contains(path, r.Error);
    }

    // 読んだ結果の表示: 直したこと・作り直したこと・使えなかったことは必ず表示し、問題が無ければ何も表示しない
    [Fact]
    public void 読めた設定は何も表示しない()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1}");

        Assert.Empty(CadPetaSettingsStore.Load(path, Now).Messages());
    }

    [Fact]
    public void 無い設定は何も表示しない()
    {
        using var temp = new TempFolder();

        Assert.Empty(CadPetaSettingsStore.Load(Path.Combine(temp.Path, "settings.json"), Now).Messages());
    }

    [Fact]
    public void 直した設定は項目と理由と残したファイルを表示する()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"pngDpi\": 5000}");

        var r = CadPetaSettingsStore.Load(path, Now);
        string text = string.Join("\n", r.Messages());

        Assert.Contains("pngDpi", text);
        Assert.Contains(CadPetaSettings.ValidatePngDpi(5000)!, text);
        Assert.Contains(r.BackupPath!, text);
    }

    [Fact]
    public void 作り直した設定は残したファイルを表示する()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "not json");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Contains(r.Messages(), m => m.Contains(r.BackupPath!));
    }

    [Fact]
    public void 新しい版の設定は既定を使うことと場所を表示する()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 99}");

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Contains(r.Messages(), m => m.Contains(path));
    }

    [Fact]
    public void 保存できない設定は場所と削除の案内を表示する()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "not json");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var r = CadPetaSettingsStore.Load(path, Now);

        Assert.Contains(r.Messages(), m => m.Contains(r.Error));
    }

    [Theory]
    [InlineData(@".\CADPeta", true)]
    [InlineData(@"C:\出力\図", true)]
    [InlineData(@"\\server\share\out", true)]
    [InlineData(@"C:\out:pdf", false)]
    [InlineData(@".\a:b", false)]
    [InlineData(@"a|b", false)]
    [InlineData(@"\out", false)]
    [InlineData(@"C:out", false)]
    [InlineData("", false)]
    public void 出力先は相対パスか完全なパスでドライブ名の位置のほかにコロンを使わない(string folder, bool ok)
    {
        Assert.Equal(ok, CadPetaSettings.ValidateOutputFolder(folder) == null);
    }

    [Theory]
    [InlineData(@".\CADPeta", @"C:\work\図面", @"C:\work\図面\CADPeta")]
    [InlineData(@"..\出力", @"C:\work\図面", @"C:\work\出力")]
    [InlineData(@"D:\出力", @"C:\work\図面", @"D:\出力")]
    [InlineData(@".", @"C:\work\図面", @"C:\work\図面")]
    [InlineData(@".\", @"C:\work\図面", @"C:\work\図面\")]
    [InlineData(@"CADPeta", @"C:\work\図面", @"C:\work\図面\CADPeta")]
    [InlineData(@".\CADPeta\", @"C:\work\図面", @"C:\work\図面\CADPeta\")]
    [InlineData(@"./CADPeta", @"C:\work\図面", @"C:\work\図面\CADPeta")]
    public void 出力先の相対パスは図面のフォルダを起点にする(string setting, string drawingDir, string expected)
    {
        Assert.Equal(expected, (CadPetaSettings.Default with { OutputFolder = setting }).ResolveOutputFolder(drawingDir));
    }

    [Fact]
    public void 保存していない図面では相対パスの起点が無い()
    {
        Assert.Null(CadPetaSettings.Default.ResolveOutputFolder(null));
        Assert.Equal(@"D:\出力", (CadPetaSettings.Default with { OutputFolder = @"D:\出力" }).ResolveOutputFolder(null));
    }
}
