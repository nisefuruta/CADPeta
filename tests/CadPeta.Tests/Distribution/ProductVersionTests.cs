using System;
using System.IO;
using System.Reflection;
using System.Xml.Linq;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests.Distribution;

/// <summary>
/// 版番号の原本は dist/bundle/CADPeta.bundle/PackageContents.xml の AppVersion の 1 か所。自作の DLL の版
/// （読み込んだときに表示する版、AssemblyVersion）は、ビルドのときにこの値から決まる（src/Directory.Build.props）。
/// </summary>
public sealed class ProductVersionTests
{
    private static string AppVersion() =>
        XDocument.Load(Path.Combine(RepositoryPaths.Root(), "dist", "bundle", "CADPeta.bundle", "PackageContents.xml"))
            .Root!.Attribute("AppVersion")!.Value;

    [Fact]
    public void 自作のDLLの版はPackageContentsのAppVersion()
    {
        var assembly = typeof(PromptTrace).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        Assert.Equal(Version.Parse(AppVersion() + ".0"), assembly.GetName().Version);
        Assert.StartsWith(AppVersion() + "+", informational);
    }
}
