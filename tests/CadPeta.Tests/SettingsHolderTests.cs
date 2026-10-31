using System;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// プラグインは設定を 1 つの SettingsHolder に持つ。最初の取得でファイルを読み、ファイルが変わるまで読み直さない。
// 直したことなどの表示は読み直したときだけ返す（PETA のたびに同じ表示を繰り返さない）。PETAOPT の保存は持っている設定も置き換える
public class SettingsHolderTests
{
    [Fact]
    public void 最初の取得でファイルを読み直したことの表示を返す()
    {
        using var temp = new TempFolder();
        var holder = new SettingsHolder(temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"pngDpi\": 5000}"));

        var got = holder.Get();

        Assert.Equal(CadPetaSettings.DefaultPngDpi, got.Settings.PngDpi);
        Assert.NotEmpty(got.Messages);
    }

    // 新しい版の設定は書き換えないので、読み直せば毎回同じ表示が出る。表示が 1 回だけなら読み直していない
    [Fact]
    public void ファイルが変わらなければ2回目の取得は同じ設定で表示を返さない()
    {
        using var temp = new TempFolder();
        var holder = new SettingsHolder(temp.WriteFile("settings.json", "{\"schemaVersion\": 99}"));
        var first = holder.Get();
        Assert.NotEmpty(first.Messages);

        var second = holder.Get();

        Assert.Equal(first.Settings, second.Settings);
        Assert.Empty(second.Messages);
    }

    [Fact]
    public void ファイルが変わったら読み直す()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"pngDpi\": 150}");
        var holder = new SettingsHolder(path);
        holder.Get();
        File.WriteAllText(path, "{\"schemaVersion\": 1, \"pngDpi\": 200}");
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));

        Assert.Equal(200, holder.Get().Settings.PngDpi);
    }

    [Fact]
    public void 保存した設定を次の取得で返し表示はしない()
    {
        using var temp = new TempFolder();
        string path = Path.Combine(temp.Path, "settings.json");
        var holder = new SettingsHolder(path);
        holder.Get();
        var saved = CadPetaSettings.Default with { PngDpi = 120 };

        Assert.Null(holder.Save(saved));
        var got = holder.Get();

        Assert.Equal(saved, got.Settings);
        Assert.Empty(got.Messages);
    }

    [Fact]
    public void 保存に失敗したら理由を返し前の設定を使い続ける()
    {
        using var temp = new TempFolder();
        string path = temp.WriteFile("settings.json", "{\"schemaVersion\": 1, \"pngDpi\": 150}");
        var holder = new SettingsHolder(path);
        holder.Get();
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.NotNull(holder.Save(CadPetaSettings.Default with { PngDpi = 120 }));
            Assert.Equal(150, holder.Get().Settings.PngDpi);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}
