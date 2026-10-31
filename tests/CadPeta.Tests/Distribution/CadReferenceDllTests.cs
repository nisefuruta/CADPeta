using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace CadPeta.Tests.Distribution;

/// <summary>
/// 配布物の検査（dist/cad_reference_dlls.ps1 の Test-CadReferenceDllName）が、src のプロジェクトが配布しない前提で参照している
/// CAD 本体の DLL を全部拒否することを確かめる。禁止の一覧を手で書くと、実際の参照からずれて検査が素通りする
/// （IJCAD の GcDbMgd.dll や AutoCAD のパッケージの AdWindows.dll が一覧に無かった）。参照の一覧は csproj から求める:
/// Private=false の Reference は HintPath のファイル名（$(…) の版は任意の値に置き換える）、ExcludeAssets=runtime の
/// PackageReference は obj/project.assets.json のそのパッケージの compile の DLL。
/// </summary>
public sealed class CadReferenceDllTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    // HintPath の $(AresNet48DllVer) などの MSBuild のプロパティの代わりに入れる版。年式ごとの名前のどれでも拒否されることを確かめる
    private const string AnyVersion = "0.0_0";

    [Fact]
    public void 配布しない参照のDLLは配布物の検査が拒否する()
    {
        var accepted = AcceptedByCheck(CompileOnlyReferences());

        Assert.True(accepted.Length == 0, "配布物の検査が拒否しない参照の DLL: " + string.Join(", ", accepted));
    }

    // 互換 CAD の参照 DLL を開発機の間で運ぶ dist/extract_vendor_refs.ps1 の一覧（年式の版の付いた実際の名前）も拒否する
    [Fact]
    public void 参照DLLの取り出しの一覧の名前も配布物の検査が拒否する()
    {
        var accepted = AcceptedByCheck(VendorReferenceFiles());

        Assert.True(accepted.Length == 0, "配布物の検査が拒否しない参照の DLL: " + string.Join(", ", accepted));
    }

    // 禁止のパターン（TD_Mgd_*.dll など）を広げすぎて、配布する自作の DLL まで拒否しないこと
    [Fact]
    public void 自作のDLLは配布物の検査が拒否しない()
    {
        var own = OwnAssemblyFiles();

        Assert.Equal(own, AcceptedByCheck(own));
    }

    // 上の試験が空の一覧で合格しないように、csproj の読み取りが各製品の参照を見つけることを確かめる
    [Fact]
    public void 配布しない参照の一覧は各製品の参照を含む()
    {
        var names = CompileOnlyReferences().ToArray();
        Assert.Contains("AcMgd.dll", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("AdWindows.dll", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("TD_Mgd_" + AnyVersion + ".dll", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("GcDbMgd.dll", names, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("BrxMgd.dll", names, StringComparer.OrdinalIgnoreCase);
    }

    // 配布物の検査（Test-CadReferenceDllName）が拒否しなかった名前を、渡した順で返す。pwsh の起動は 1 回
    static string[] AcceptedByCheck(IEnumerable<string> dllNames)
    {
        string script = Path.Combine(RepositoryPaths.Root(), "dist", "cad_reference_dlls.ps1");
        var result = ProcessRunner.RunPowerShell("pwsh",
            new[] { "-NoProfile", "-NonInteractive", "-Command",
                "& { . $env:CP_SCRIPT; $env:CP_DLLS -split '\\|' | Where-Object { -not (Test-CadReferenceDllName $_) } }" },
            Timeout, new Dictionary<string, string> { ["CP_SCRIPT"] = script, ["CP_DLLS"] = string.Join("|", dllNames) });
        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        return result.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
    }

    static string[] VendorReferenceFiles()
    {
        string text = File.ReadAllText(Path.Combine(RepositoryPaths.Root(), "dist", "extract_vendor_refs.ps1"));
        var names = Regex.Matches(text, @"Files = @\(([^)]*)\)").Cast<Match>()
            .SelectMany(m => Regex.Matches(m.Groups[1].Value, @"""([^""]+\.dll)""").Cast<Match>().Select(n => n.Groups[1].Value)).ToArray();
        Assert.NotEmpty(names);
        return names;
    }

    // src のプロジェクトが作る自作の DLL の名前（プロジェクトのファイル名と同じアセンブリ名）
    static string[] OwnAssemblyFiles() =>
        Directory.GetFiles(Path.Combine(RepositoryPaths.Root(), "src"), "CadPeta.*.csproj", SearchOption.AllDirectories)
            .Select(project => Path.GetFileNameWithoutExtension(project) + ".dll").OrderBy(name => name, StringComparer.Ordinal).ToArray();
    static IEnumerable<string> CompileOnlyReferences()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in Directory.GetFiles(Path.Combine(RepositoryPaths.Root(), "src"), "*.csproj", SearchOption.AllDirectories))
        {
            var xml = XDocument.Load(project);
            foreach (var reference in xml.Descendants("Reference").Where(r => (string?)r.Element("Private") == "false"))
            {
                string hint = (string?)reference.Element("HintPath") ?? throw new InvalidDataException($"Private=false の Reference に HintPath が無い: {project}");
                names.Add(Regex.Replace(Path.GetFileName(hint.Replace('\\', '/')), @"\$\([^)]*\)", AnyVersion));
            }
            var packages = xml.Descendants("PackageReference")
                .Where(p => ((string?)p.Attribute("ExcludeAssets") ?? "").IndexOf("runtime", StringComparison.OrdinalIgnoreCase) >= 0)
                .Select(p => (string)p.Attribute("Include")!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (packages.Count == 0) continue;
            string assets = Path.Combine(Path.GetDirectoryName(project)!, "obj", "project.assets.json");
            if (!File.Exists(assets))
                throw new InvalidOperationException($"ExcludeAssets=runtime のパッケージの DLL の名前を読めない（restore していない）: {assets}。先に dotnet restore CadPeta.sln を実行する");
            using var json = JsonDocument.Parse(File.ReadAllText(assets));
            foreach (var target in json.RootElement.GetProperty("targets").EnumerateObject())
                foreach (var package in target.Value.EnumerateObject().Where(p => packages.Contains(p.Name.Split('/')[0])))
                    if (package.Value.TryGetProperty("compile", out var compile))
                        foreach (var file in compile.EnumerateObject().Select(f => Path.GetFileName(f.Name)).Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                            names.Add(file);
        }
        return names;
    }
}
