using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public sealed class BuildStampTests
{
    [Fact]
    public void Format_LongSha_ShortensToEightChars()
    {
        string text = BuildStamp.Format("1.0.0+0456f0941d7bc642802d9d865d0da41daf4d423f",
            "Debug", ".NETCoreApp,Version=v8.0", "C:\\dir\\x.dll", includePath: false);
        Assert.Contains("1.0.0+0456f094", text);
        Assert.DoesNotContain("0456f0941d7bc642802d9d865d0da41daf4d423f", text);
    }

    [Fact]
    public void Format_NoPlus_PassesThroughUnchanged()
    {
        string text = BuildStamp.Format("1.0.0", "Debug", ".NETCoreApp,Version=v8.0",
            "C:\\dir\\x.dll", includePath: false);
        Assert.Contains("1.0.0", text);
    }

    [Fact]
    public void Format_ShaShorterThanEight_PassesThroughUnchanged()
    {
        string text = BuildStamp.Format("1.0.0+abc", "Debug", ".NETCoreApp,Version=v8.0",
            "C:\\dir\\x.dll", includePath: false);
        Assert.Contains("1.0.0+abc", text);
    }

    [Fact]
    public void Format_EmptyLocationWithIncludePath_FallsBackWithoutThrowing()
    {
        string text = BuildStamp.Format("1.0.0", "Debug", ".NETCoreApp,Version=v8.0",
            "", includePath: true);
        Assert.Contains("読み込み元:", text);
    }

    [Fact]
    public void Format_IncludePathFalse_OmitsLocationEntirely()
    {
        string text = BuildStamp.Format("1.0.0", "Debug", ".NETCoreApp,Version=v8.0",
            @"C:\secret\dir\x.dll", includePath: false);
        Assert.DoesNotContain("読み込み元:", text);
        Assert.DoesNotContain(@"C:\secret\dir\x.dll", text);
    }

    [Fact]
    public void Format_AllNull_FallsBackWithoutThrowing()
    {
        string text = BuildStamp.Format(null, null, null, null, includePath: false);
        Assert.Contains("(版数不明)", text);
        Assert.Contains("(構成不明)", text);
        Assert.Contains("(TFM不明)", text);
    }

    [Fact]
    public void Describe_WithoutPath_ContainsConfigurationButNoPath()
    {
        string text = BuildStamp.Describe(typeof(BuildStampTests).Assembly, includePath: false);
        Assert.False(string.IsNullOrEmpty(text));
        Assert.True(text.Contains("Debug") || text.Contains("Release"));
        Assert.DoesNotContain("読み込み元:", text);
    }

    [Fact]
    public void Describe_WithPath_ContainsLoadedFrom()
    {
        string text = BuildStamp.Describe(typeof(BuildStampTests).Assembly, includePath: true);
        Assert.Contains("読み込み元:", text);
    }
}
