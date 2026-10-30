using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace CadPeta.Tests.Distribution;

/// <summary>
/// 配布物の install.bat と uninstall.bat を、子プロセスの APPDATA だけを試験のフォルダへ向けて実行し、約束を確かめる。
/// 利用者の実際の APPDATA の install.bat・uninstall.bat は実行しない。AutoCAD は起動しない(DLL の中身はダミー)。
/// </summary>
public sealed class InstallerBatchTests
{
    private static readonly string BundleRoot = Path.Combine(RepositoryPaths.Root(), "dist", "bundle");

    /// <summary>bundle の DLL のフォルダ(PackageContents.xml の ModuleName の Contents\&lt;フォルダ&gt;)。</summary>
    private static readonly string[] TfmFolders = { "net48", "net8", "net10" };

    private const string PluginDll = "CadPeta.Plugin.Acad.dll";

    private const string LangFile = "1041.lang";

    // ---------------------------------------------------------------- bat の書式

    [Theory]
    [InlineData("install.bat")]
    [InlineData("uninstall.bat")]
    public void バッチはASCIIだけとCRLFでコードページを切り替えない(string name)
    {
        // 日本語の環境でも cmd を 65001 や 437 で開いている利用者がいて、日本語や UTF-8 の bat は行を読み誤る。表示は英語にし、日本語の説明は説明書に書く。
        var bytes = File.ReadAllBytes(Path.Combine(BundleRoot, name));
        Assert.True(bytes.All(value => value < 0x80), name + " に ASCII 以外のバイトがある: " + Array.FindIndex(bytes, value => value >= 0x80));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.EndsWith("\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
        Assert.DoesNotContain(text.Split(new[] { "\r\n" }, StringSplitOptions.None),
            line => !line.TrimStart().StartsWith("rem ", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(line, @"\bchcp\b", RegexOptions.IgnoreCase));
    }

    /// <summary>
    /// 使用中の DLL の確認と、フォルダの改名・移動・削除のやり直しは、2 つの bat で同じ手順にする(片方だけ直すと、同じ失敗がもう片方に残る)。
    /// </summary>
    [Theory]
    [InlineData("DLL check", "rem ---- end of the DLL check")]
    [InlineData("folder operations", "rem ---- end of the folder operations")]
    public void 共通の手順の区間はinstallとuninstallで同じ内容である(string section, string endMarker)
    {
        string Section(string name)
        {
            var text = File.ReadAllText(Path.Combine(BundleRoot, name), Encoding.ASCII);
            // 区間の見出しは「rem ---- <区間の名前>:」(先頭のコメントにも区間の名前が出るので、コロンまで含めて探す)
            var start = text.IndexOf("rem ---- " + section + ":", StringComparison.Ordinal);
            var end = text.IndexOf(endMarker, StringComparison.Ordinal);
            Assert.True(start >= 0 && end > start, name + " に区間(rem ---- " + section + ": から " + endMarker + " まで)が無い");
            return text.Substring(start, end - start);
        }
        Assert.Equal(Section("install.bat"), Section("uninstall.bat"));
    }

    // ---------------------------------------------------------------- インストール・更新・アンインストール

    [Fact]
    public void 初回インストールと更新で旧版だけのファイルを消しlangも新しい版にしAPPDATAのほかのファイルは残す()
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.AssertInstalled("新版");
        File.WriteAllText(Path.Combine(box.Destination, "Contents", "net8", "旧版だけ.dll"), "削除対象");
        box.WriteSource("更新版");
        box.Run("install.bat", 0);
        box.AssertInstalled("更新版");
        Assert.False(File.Exists(Path.Combine(box.Destination, "Contents", "net8", "旧版だけ.dll")));
        box.AssertOtherAppData();
    }

    [Fact]
    public void アンインストールはlangを含めて全部削除しAPPDATAのほかのファイルは残し2回目も成功する()
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.Run("uninstall.bat", 0);
        Assert.False(Directory.Exists(box.Destination));
        Assert.False(Directory.Exists(box.Old));
        Assert.False(Directory.Exists(box.Staging));
        box.AssertOtherAppData();
        Assert.Contains("Nothing to remove", box.Run("uninstall.bat", 0));
    }

    [Fact]
    public void コピー先のファイルのパスが260文字を超えてもインストールと更新とアンインストールができる()
    {
        // ZIP を深いフォルダに解凍したときや、APPDATA が長いとき。xcopy はファイルのパスが 260 文字以上になると「メモリが足りません」で失敗する。
        // 仮の AppData の名前の長さで、CADPeta.bundle.new へコピーする最も長いファイルのパスを 270 文字にする(試験のフォルダの場所によらない)。
        var copied = Path.Combine("Autodesk", "CADPeta.bundle.new", "Contents", "net10", "lang", LangFile);
        var prefix = "長いパス ";
        var name = prefix + new string('d', Math.Max(1, 270 - (InstallerSandbox.PathLength + 2 + copied.Length + prefix.Length)));
        using var box = new InstallerSandbox(name);
        Assert.True(Path.Combine(box.Staging, "Contents", "net10", "lang", LangFile).Length >= 270);
        box.Run("install.bat", 0);
        box.AssertInstalled("新版");
        box.WriteSource("更新版");
        box.Run("install.bat", 0);
        box.AssertInstalled("更新版");
        box.Run("uninstall.bat", 0);
        Assert.False(Directory.Exists(box.Destination));
        Assert.False(Directory.Exists(box.Old));
        Assert.False(Directory.Exists(box.Staging));
        box.AssertOtherAppData();
    }

    // ---------------------------------------------------------------- 使用中の DLL・展開の欠け

    [Theory]
    [InlineData("install.bat")]
    [InlineData("uninstall.bat")]
    public void 使用中のDLLがあれば何も変えずに理由を示して失敗する(string script)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.WriteSource("更新版");
        var before = box.Snapshot();
        // bat が終わるまで開いたままにする(DLL の確認のやり直しの約 10 秒より長い)
        using (new FileStream(Path.Combine(box.Destination, "Contents", "net8", PluginDll), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Contains("An installed CADPeta file cannot be opened", box.Run(script, 1));
        }
        Assert.Equal(before, box.Snapshot());
    }

    /// <summary>
    /// AutoCAD はウィンドウを閉じたあとも数秒間 DLL を開いている(PurgeKit で AutoCAD 2027 を測ると、DLL を書き込みで開けるまで約 2.3 秒)。
    /// AutoCAD を閉じた直後に実行しても、bat は DLL が開放されるのを待って成功する。待っている表示がちょうど 1 回出ることも確かめ、
    /// bat が確認する前に開放が終わって偶然合格する場合を除く。
    /// </summary>
    [Theory]
    [InlineData("install.bat")]
    [InlineData("uninstall.bat")]
    public void AutoCADを閉じた直後でDLLが数秒開いていてもインストールとアンインストールは成功する(string script)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.WriteSource("更新版");
        string output;
        using (var hold = FileHold.Start(Path.Combine(box.Destination, "Contents", "net8", PluginDll), TimeSpan.FromSeconds(3), FileShare.Read))
        {
            hold.WaitOpened();
            output = box.Run(script, 0);
        }
        Assert.Single(Regex.Matches(output, Regex.Escape("Waiting for AutoCAD to finish closing")));
        if (script == "uninstall.bat")
        {
            Assert.False(Directory.Exists(box.Destination));
            Assert.False(Directory.Exists(box.Old));
            Assert.False(Directory.Exists(box.Staging));
        }
        else box.AssertInstalled("更新版");
        box.AssertOtherAppData();
    }

    public enum BrokenList { MissingList, EmptyList, MissingFile }

    [Theory]
    [InlineData(BrokenList.MissingList)]
    [InlineData(BrokenList.EmptyList)]
    [InlineData(BrokenList.MissingFile)]
    public void 展開に欠けがあれば何も変えずに理由を示して失敗する(BrokenList kind)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.WriteSource("更新版");
        switch (kind)
        {
            case BrokenList.MissingList: File.Delete(box.ListPath); break;
            case BrokenList.EmptyList: File.WriteAllText(box.ListPath, ""); break;
            default: File.Delete(Path.Combine(box.Source, "Contents", "net10", "lang", LangFile)); break;
        }
        var before = box.Snapshot();
        var output = box.Run("install.bat", 1);
        Assert.Contains("Extract all files of the zip", output);
        Assert.Equal(before, box.Snapshot());
    }

    // ---------------------------------------------------------------- ほかのプログラムが開いているファイル

    /// <summary>ほかのプログラムが一時的にファイルを開いているフォルダ(試験の場面)。</summary>
    public enum HeldFolder { InstallRenameInstalled, InstallMoveNew, InstallDeleteLeftoverNew, InstallDeleteStaleOld, InstallRestoreOld, UninstallRenameInstalled, UninstallDeleteOld }

    /// <summary>
    /// コピーした直後のファイルは、ウイルス対策ソフト・Windows Search・同期のソフトなどが数秒間開いていることがあり、その間はフォルダの改名・移動・削除が失敗する
    /// (PlotEarth で 2 回、PurgeKit で 1 回。もう一度実行すると成功した)。フォルダの中のファイルを削除の共有を許さずに 3 秒間開いたままにしても、
    /// インストールとアンインストールは成功する。
    /// </summary>
    [Theory]
    [InlineData(HeldFolder.InstallRenameInstalled)]
    [InlineData(HeldFolder.InstallMoveNew)]
    [InlineData(HeldFolder.InstallDeleteLeftoverNew)]
    [InlineData(HeldFolder.InstallDeleteStaleOld)]
    [InlineData(HeldFolder.InstallRestoreOld)]
    [InlineData(HeldFolder.UninstallRenameInstalled)]
    [InlineData(HeldFolder.UninstallDeleteOld)]
    public void ほかのプログラムがフォルダのファイルを数秒開いていてもインストールとアンインストールは成功する(HeldFolder where)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.WriteSource("更新版");
        string held;
        switch (where)
        {
            case HeldFolder.InstallRenameInstalled:
            case HeldFolder.UninstallRenameInstalled:
                held = Path.Combine(box.Destination, "PackageContents.xml");
                break;
            case HeldFolder.InstallMoveNew:
                // install.bat がコピーしたあとで開く(コピーの直後に検査されるのと同じ)
                held = Path.Combine(box.Staging, "PackageContents.xml");
                break;
            case HeldFolder.InstallDeleteLeftoverNew:
                Directory.CreateDirectory(box.Staging);
                held = Path.Combine(box.Staging, "途中.txt");
                File.WriteAllText(held, "途中");
                break;
            case HeldFolder.InstallDeleteStaleOld:
                Directory.CreateDirectory(box.Old);
                held = Path.Combine(box.Old, "古いバックアップ.txt");
                File.WriteAllText(held, "古い");
                break;
            case HeldFolder.UninstallDeleteOld:
                // uninstall.bat が CADPeta.bundle を .old へ改名したあとで、その中の lang のファイルを開く
                held = Path.Combine(box.Old, "Contents", "net10", "lang", LangFile);
                break;
            default:
                // 前回のインストールが、既存の版を .old にしたところで中断した
                Directory.Move(box.Destination, box.Old);
                held = Path.Combine(box.Old, "PackageContents.xml");
                break;
        }
        var script = where is HeldFolder.UninstallRenameInstalled or HeldFolder.UninstallDeleteOld ? "uninstall.bat" : "install.bat";
        using (var hold = FileHold.Start(held, TimeSpan.FromSeconds(3)))
        {
            // 前からあるファイルは、開いたのを確かめてから始める。bat が作るファイルは、できたら開く
            if (where is not (HeldFolder.InstallMoveNew or HeldFolder.UninstallDeleteOld)) hold.WaitOpened();
            box.Run(script, 0);
            Assert.True(hold.WasOpened, "試験のためのファイルを開けなかった: " + held);
        }
        if (script == "uninstall.bat")
        {
            Assert.False(Directory.Exists(box.Destination));
            Assert.False(Directory.Exists(box.Old));
        }
        else box.AssertInstalled("更新版");
        box.AssertOtherAppData();
    }

    /// <summary>
    /// やり直しの時間(約 10 秒)より長くファイルが開かれたままなら、bat は失敗として止まり、既存の版を残して、Windows の理由と対処を示す。
    /// 新しい版の移動で止まる場合は、移動のやり直し(約 10 秒)のあとに .new の削除のやり直し(約 10 秒)が続くので、ファイルは bat が終わるまで
    /// 開いたままにし、bat が終わった時点でまだ開いていることを確かめる(短いと、.new が残るかどうかが時間で変わる)。
    /// </summary>
    [Theory]
    [InlineData(HeldFolder.InstallRenameInstalled, "install.bat", "Could not rename the existing installation")]
    [InlineData(HeldFolder.InstallMoveNew, "install.bat", "Could not move the new version into place")]
    [InlineData(HeldFolder.UninstallRenameInstalled, "uninstall.bat", "Could not remove")]
    public void ほかのプログラムがファイルを開いたままなら既存の版を残して理由を示し失敗する(HeldFolder where, string script, string expected)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        box.WriteSource("更新版");
        var held = where == HeldFolder.InstallMoveNew ? Path.Combine(box.Staging, "PackageContents.xml") : Path.Combine(box.Destination, "PackageContents.xml");
        string output;
        using (var hold = FileHold.Start(held, TimeSpan.FromSeconds(35)))
        {
            if (where != HeldFolder.InstallMoveNew) hold.WaitOpened();
            output = box.Run(script, 1);
            Assert.True(hold.WasOpened, "試験のためのファイルを開けなかった: " + held);
            Assert.True(hold.IsHolding, script + " が終わる前にファイルを閉じた(開いておく時間が足りない)");
        }
        Assert.Contains("Another program is using a file in the folder", output);
        // Windows の理由(日本語の Windows では日本語)。ren・move は「アクセスが拒否されました」、rmdir は「別のプロセスが使用中です」
        Assert.Matches("Access is denied|アクセスが拒否されました|being used by another process|別のプロセスが使用中です", output);
        // move の結果の件数(「0 個のディレクトリを移動しました。」)は表示しない
        Assert.DoesNotMatch(@"dir\(s\) moved|ディレクトリを移動しました", output);
        Assert.Contains(expected, output);
        box.AssertInstalled("新版", allowStaging: where == HeldFolder.InstallMoveNew);
        if (where == HeldFolder.InstallMoveNew)
        {
            Assert.Contains("The following folder could not be deleted. Delete it yourself:", output);
            Assert.Contains(Environment.NewLine + "    " + box.Staging + Environment.NewLine, output);
        }
        box.AssertOtherAppData();
    }

    // ---------------------------------------------------------------- アンインストールが削除するフォルダ

    /// <summary>
    /// CADPeta.bundle の名前のフォルダは、PackageContents.xml があるかどうかだけで、インストール済みの製品か残骸かを判定する。
    /// PackageContents.xml の Name と ProductCode は読まず、違っていてもインストール済みの製品として削除する。
    /// </summary>
    [Fact]
    public void アンインストールはNameとProductCodeが違ってもPackageContentsのあるCADPeta_bundleを削除する()
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        var manifest = Path.Combine(box.Destination, "PackageContents.xml");
        File.WriteAllText(manifest, File.ReadAllText(manifest)
            .Replace("{F79DD514-154F-4F75-AB3A-1EF016F21D67}", "{00000000-0000-0000-0000-000000000000}")
            .Replace("Name=\"CADPeta\"", "Name=\"Other\""));
        Assert.Contains("Uninstall succeeded", box.Run("uninstall.bat", 0));
        Assert.False(Directory.Exists(box.Destination));
        Assert.False(Directory.Exists(box.Old));
        box.AssertOtherAppData();
    }

    // ---------------------------------------------------------------- 遅延展開

    /// <summary>
    /// 親の cmd が遅延展開を有効にして起動されていても(/V:ON。レジストリの DelayedExpansion が 1 のときも同じ)、bat はパスの ! を消さずに扱う。
    /// 遅延展開が有効のままだと、set "PARENT=%APPDATA%\..." の対になっていない ! が消え、別のフォルダへインストールする。
    /// </summary>
    [Fact]
    public void 親のcmdが遅延展開を有効にしていても感嘆符を含むAPPDATAでインストールと更新とアンインストールができる()
    {
        using var box = new InstallerSandbox("App!Data(x)");
        box.Run("install.bat", 0, delayedExpansion: true);
        box.AssertInstalled("新版");
        box.WriteSource("更新版");
        box.Run("install.bat", 0, delayedExpansion: true);
        box.AssertInstalled("更新版");
        box.Run("uninstall.bat", 0, delayedExpansion: true);
        Assert.False(Directory.Exists(box.Destination));
        Assert.False(Directory.Exists(box.Old));
        Assert.False(Directory.Exists(box.Staging));
        box.AssertOtherAppData();
    }

    /// <summary>
    /// 残骸はフォルダの名前だけで判定し、中身を照らさない(インストール先を残さずクリーンに保つ)。
    /// PackageContents.xml の無い CADPeta.bundle は、利用者が置いたファイルや CADPeta の DLL の有無にかかわらず削除する。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void アンインストールはPackageContentsの無いCADPeta_bundleを中身にかかわらず削除する(bool withoutPluginDll)
    {
        using var box = new InstallerSandbox();
        // 前回のアンインストールが、PackageContents.xml を削除したあとで止まった
        box.Run("install.bat", 0);
        File.Delete(Path.Combine(box.Destination, "PackageContents.xml"));
        File.WriteAllText(Path.Combine(box.Destination, "利用者のメモ.txt"), "利用者のファイル");
        if (withoutPluginDll)
        {
            foreach (var dll in Directory.GetFiles(box.Destination, "*.dll", SearchOption.AllDirectories))
                File.Delete(dll);
        }
        box.Run("uninstall.bat", 0);
        Assert.False(Directory.Exists(box.Destination));
        Assert.False(Directory.Exists(box.Old));
        box.AssertOtherAppData();
    }

    [Theory]
    [InlineData("install.bat")]
    [InlineData("uninstall.bat")]
    public void 利用者のファイルを入れたoldも削除する(string script)
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        // 前回のアンインストールが .old への改名と PackageContents.xml の削除のあとで止まり、利用者がファイルを置いた
        Directory.Move(box.Destination, box.Old);
        File.Delete(Path.Combine(box.Old, "PackageContents.xml"));
        File.WriteAllText(Path.Combine(box.Old, "利用者のメモ.txt"), "利用者のファイル");
        box.Run(script, 0);
        Assert.False(Directory.Exists(box.Old));
        Assert.Equal(script == "install.bat", Directory.Exists(box.Destination));
    }

    [Fact]
    public void PackageContentsの無いCADPeta_bundleのDLLが使用中なら何も削除しない()
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        File.Delete(Path.Combine(box.Destination, "PackageContents.xml"));
        File.WriteAllText(Path.Combine(box.Destination, "利用者のメモ.txt"), "利用者のファイル");
        var before = box.Snapshot();
        // bat が終わるまで開いたままにする(DLL の確認のやり直しの約 10 秒より長い)
        using (new FileStream(Path.Combine(box.Destination, "Contents", "net8", PluginDll), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Contains("An installed CADPeta file cannot be opened", box.Run("uninstall.bat", 1));
        }
        Assert.Equal(before, box.Snapshot());
    }

    /// <summary>
    /// アンインストールは PackageContents.xml を先に削除する(途中で止まったフォルダを、install.bat が以前の版と取り違えないように)。
    /// それを削除できないときは、ほかに何も削除せず、フォルダの名前を CADPeta.bundle に戻して、Windows の理由を示して失敗する。
    /// </summary>
    [Fact]
    public void アンインストールはPackageContentsを削除できないとき原因を示しフォルダ名を戻して失敗する()
    {
        using var box = new InstallerSandbox();
        box.Run("install.bat", 0);
        var manifest = Path.Combine(box.Destination, "PackageContents.xml");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        // 試験のフォルダの中のファイルにだけ、自分への削除の拒否を設定する。後で必ず解除する。
        var denyFile = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.Delete, System.Security.AccessControl.AccessControlType.Deny);
        var denyChild = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.DeleteSubdirectoriesAndFiles, System.Security.AccessControl.AccessControlType.Deny);
        var fileSecurity = new FileInfo(manifest).GetAccessControl();
        fileSecurity.AddAccessRule(denyFile);
        new FileInfo(manifest).SetAccessControl(fileSecurity);
        var directorySecurity = new DirectoryInfo(box.Destination).GetAccessControl();
        directorySecurity.AddAccessRule(denyChild);
        new DirectoryInfo(box.Destination).SetAccessControl(directorySecurity);
        string output;
        try
        {
            output = box.Run("uninstall.bat", 1);
        }
        finally
        {
            var restoredFile = new FileInfo(manifest);
            var security = restoredFile.GetAccessControl();
            Assert.True(security.RemoveAccessRule(denyFile));
            restoredFile.SetAccessControl(security);
            var restoredDirectory = new DirectoryInfo(box.Destination);
            var directoryRestore = restoredDirectory.GetAccessControl();
            Assert.True(directoryRestore.RemoveAccessRule(denyChild));
            restoredDirectory.SetAccessControl(directoryRestore);
        }
        Assert.Contains("Could not delete PackageContents.xml", output);
        Assert.Contains("The folder was renamed back to CADPeta.bundle", output);
        Assert.DoesNotContain("AutoCAD is probably running with CADPeta loaded", output);
        box.AssertInstalled("新版");
        box.AssertOtherAppData();
    }

    // ---------------------------------------------------------------- 試験の補助

    /// <summary>
    /// ファイルを読み取りで開き、決めた時間だけ開いたままにする。ファイルが無ければ、できるまで待つ。
    /// 既定の共有(読み書きを許し、削除を許さない)は、ほかのプログラムがファイルを検査している間と同じ。
    /// <see cref="FileShare.Read"/> は、AutoCAD が読み込んだ DLL と同じ(書き込みで開けない)。
    /// </summary>
    private sealed class FileHold : IDisposable
    {
        private readonly ManualResetEventSlim opened = new();
        private readonly Task task;
        public bool WasOpened => opened.IsSet;
        public bool IsHolding => opened.IsSet && !task.IsCompleted;

        private FileHold(string path, TimeSpan duration, FileShare share) => task = Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, share);
                    opened.Set();
                    Thread.Sleep(duration);
                    return;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException or UnauthorizedAccessException)
                {
                    // まだできていない(bat が作る前)。できるまで待つ
                }
            }
        });

        public static FileHold Start(string path, TimeSpan duration, FileShare share = FileShare.ReadWrite) => new(path, duration, share);

        public void WaitOpened() => Assert.True(opened.Wait(TimeSpan.FromSeconds(10)), "試験のためのファイルを開けなかった");

        public void Dispose()
        {
            task.Wait(TimeSpan.FromSeconds(40));
            opened.Dispose();
        }
    }

    /// <summary>
    /// 試験のフォルダ。zip を展開した場所(install.bat・uninstall.bat・bundle-files.txt・CADPeta.bundle)と、仮の AppData を持つ。
    /// フォルダの名前は短くする(パスの長さを試験が決められるように)。
    /// </summary>
    private sealed class InstallerSandbox : IDisposable
    {
        private readonly TempFolder folder = new(TempFolder.ShortIdLength);
        private readonly string appData;
        private readonly string otherFile;

        /// <summary>試験のフォルダのパスの長さ。</summary>
        public static int PathLength => TempFolder.Root.Length + 1 + TempFolder.ShortIdLength;

        public string Source { get; }
        public string Destination { get; }
        public string Old => Destination + ".old";
        public string Staging { get; }
        public string ListPath => folder.File("bundle-files.txt");

        public InstallerSandbox(string appDataName = "仮の AppData")
        {
            appData = Path.Combine(folder.Path, appDataName);
            Source = folder.File("CADPeta.bundle");
            Destination = Path.Combine(appData, "Autodesk", "ApplicationPlugins", "CADPeta.bundle");
            Staging = Path.Combine(appData, "Autodesk", "CADPeta.bundle.new");
            // APPDATA のほかのプログラムのファイル(インストールとアンインストールで変わらないこと)
            otherFile = Path.Combine(appData, "Autodesk", "ApplicationPlugins", "Other.bundle", "PackageContents.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(otherFile)!);
            File.WriteAllText(otherFile, "ほかのプラグイン");
            foreach (var name in new[] { "install.bat", "uninstall.bat" }) File.Copy(Path.Combine(BundleRoot, name), folder.File(name));
            WriteSource("新版");
        }

        /// <summary>zip の CADPeta.bundle を作り直す。DLL と lang の中身は <paramref name="value"/>(AutoCAD やプラグインを起動しない)。</summary>
        public void WriteSource(string value)
        {
            if (Directory.Exists(Source)) Directory.Delete(Source, recursive: true);
            Directory.CreateDirectory(Source);
            File.Copy(Path.Combine(BundleRoot, "CADPeta.bundle", "PackageContents.xml"), Path.Combine(Source, "PackageContents.xml"));
            foreach (var tfm in TfmFolders)
            {
                var directory = Path.Combine(Source, "Contents", tfm);
                Directory.CreateDirectory(Path.Combine(directory, "lang"));
                File.WriteAllText(Path.Combine(directory, PluginDll), value);
                File.WriteAllText(Path.Combine(directory, "UglyToad.PdfPig.dll"), value);
                File.WriteAllText(Path.Combine(directory, "lang", LangFile), value);
            }
            WriteFileList();
        }

        // bundle-files.txt は、配布物の組み立て(build_dist.ps1)と同じ dist/dist_docs.ps1 の Write-BundleFileList で作る(書式を試験に書き写さない)。
        private void WriteFileList()
        {
            var result = ProcessRunner.RunPowerShell("pwsh",
                new[] { "-NoProfile", "-NonInteractive", "-Command", "$ErrorActionPreference = 'Stop'; . $env:CP_DIST_DOCS; Write-BundleFileList $env:CP_ZIP_ROOT" },
                TimeSpan.FromSeconds(60),
                new Dictionary<string, string>
                {
                    ["CP_DIST_DOCS"] = Path.Combine(RepositoryPaths.Root(), "dist", "dist_docs.ps1"),
                    ["CP_ZIP_ROOT"] = folder.Path,
                });
            Assert.True(result.ExitCode == 0, "Write-BundleFileList の終了コード: " + result.ExitCode + "\n" + result.Output + result.Error);
        }

        /// <summary>仮の AppData のファイルとフォルダの一覧(名前と SHA256。<see cref="FolderSnapshot"/>)。インストール済みの内容と APPDATA が変わっていないことの比較に使う。</summary>
        public string[] Snapshot() => FolderSnapshot.Of(appData);

        /// <summary>
        /// bat を cmd で実行する。APPDATA は子プロセスにだけ渡し、試験のプロセスや利用者の環境変数は変えない。
        /// 標準入力には改行を渡す(pause で止まらないように)。出力は、cmd がコードページを切り替えないので OEM のコードページで読む。
        /// <paramref name="delayedExpansion"/> は、親の cmd を遅延展開を有効にして(/V:ON)起動する。
        /// </summary>
        public string Run(string script, int expectedExitCode, bool delayedExpansion = false)
        {
            var arguments = delayedExpansion ? new[] { "/d", "/V:ON", "/c", folder.File(script) } : new[] { "/d", "/c", folder.File(script) };
            var result = ProcessRunner.Run(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                arguments, TimeSpan.FromSeconds(90), ConsoleEncoding,
                new Dictionary<string, string> { ["APPDATA"] = appData }, folder.Path, Environment.NewLine);
            var output = result.Output + result.Error;
            Assert.True(result.ExitCode == expectedExitCode, script + " の終了コード: " + result.ExitCode + "\n" + output);
            return output;
        }

        private static readonly Encoding ConsoleEncoding = ProcessRunner.OemEncoding();

        /// <summary>インストール先に <paramref name="expected"/> の版が入り、作業用のフォルダ(.old、.new)が残っていない。</summary>
        public void AssertInstalled(string expected, bool allowStaging = false)
        {
            Assert.True(File.Exists(Path.Combine(Destination, "PackageContents.xml")));
            foreach (var tfm in TfmFolders)
            {
                Assert.Equal(expected, File.ReadAllText(Path.Combine(Destination, "Contents", tfm, PluginDll)));
                Assert.Equal(expected, File.ReadAllText(Path.Combine(Destination, "Contents", tfm, "lang", LangFile)));
            }
            Assert.False(Directory.Exists(Old));
            if (!allowStaging) Assert.False(Directory.Exists(Staging));
        }

        public void AssertOtherAppData() => Assert.Equal("ほかのプラグイン", File.ReadAllText(otherFile));

        public void Dispose() => folder.Dispose();
    }
}
