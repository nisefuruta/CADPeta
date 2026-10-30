using System;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public sealed class PetaOutcomeTests
{
    static readonly DateTime Now = new(2026, 7, 12, 14, 35, 2);

    static string Report(bool diagnostic, bool fileOutput, string emfPath, string? clipError,
                         string? recovered, long emfBytes, int nodeCount,
                         bool groupByLayer = false, bool shx = false, bool textOutline = false,
                         double margin = 0)
    {
        var sw = new StringWriter();
        PetaOutcome.Report(Msg.Japanese, sw, diagnostic, fileOutput, emfPath, clipError, recovered,
            emfBytes, nodeCount, groupByLayer, shx, textOutline, margin);
        return sw.ToString();
    }

    [Fact]
    public void Report_Success_WritesOnlySummary()
    {
        string text = Report(false, true, @"C:\out\dwg_Model_20260712_143502.emf",
            null, null, 15204, 47, groupByLayer: true);
        Assert.Contains("dwg_Model_20260712_143502.emf", text);
        Assert.Contains("15,204 bytes / 47図形 → クリップボード登録", text);
        Assert.Contains("オプション: レイヤ別グループ化", text);
        Assert.DoesNotContain("失敗", text);
        Assert.DoesNotContain("系統", text);
    }

    [Fact]
    public void Report_Diagnostic_WritesNothing()
    {
        Assert.Equal("", Report(true, true, @"C:\out\x.emf", null, null, 1, 1).Trim());
    }

    [Fact]
    public void Report_ClipboardFailure_GuidesToEmfAndReportsFailureInSummary()
    {
        string text = Report(false, true, @"C:\out\x.emf", "クリップボードを開けない", null, 100, 2);
        Assert.Contains("クリップボード登録に失敗した: クリップボードを開けない", text);
        Assert.Contains(@"利用可能な EMF: C:\out\x.emf", text);
        Assert.Contains("クリップボード登録失敗", text); // 結果サマリ側
    }

    [Fact]
    public void Report_ClipboardFailure_PrefersRecoveredPathWhenGiven()
    {
        string text = Report(false, false, @"C:\tmp\x.emf", "busy", @"C:\recovered\x.emf", 100, 2);
        Assert.Contains(@"利用可能な EMF: C:\recovered\x.emf", text);
        Assert.DoesNotContain(@"利用可能な EMF: C:\tmp\x.emf", text);
    }

    [Fact]
    public void RecoverIfNeeded_FileOutputOn_ReturnsNullWithoutTouchingFiles()
    {
        using var scope = ArtifactScope.CreateTemp("d", "l", Now);
        string? r = PetaOutcome.RecoverIfNeeded(scope, Path.Combine(scope.Dir, "x.emf"),
                                                effectiveFileOutput: true, warn: _ => { });
        Assert.Null(r);
    }

    [Fact]
    public void RecoverIfNeeded_FileOutputOff_MovesEmfOutOfWorkDir()
    {
        using var scope = ArtifactScope.CreateTemp("d", "l", Now);
        string emf = Path.Combine(scope.Dir, "d_l.emf");
        File.WriteAllBytes(emf, MinimalArtifactBytes.Emf());
        string? r = PetaOutcome.RecoverIfNeeded(scope, emf, effectiveFileOutput: false, warn: _ => { });
        try
        {
            Assert.NotNull(r);
            Assert.False(File.Exists(emf)); // 作業フォルダから退避された
            Assert.True(File.Exists(r));
        }
        finally { if (r != null && File.Exists(r)) File.Delete(r); }
    }

}
