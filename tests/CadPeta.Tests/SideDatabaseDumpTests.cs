using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class SideDatabaseDumpTests
{
    static SideDatabaseMetadata Snapshot(double minX = 1, int lineCount = 2,
        int unavailable = 0) => new()
    {
        EntityCount = lineCount,
        EntityTypes = new Dictionary<string, int> { ["Line"] = lineCount },
        ExtentsUnavailableCount = unavailable,
        Extents = new SideDatabaseExtents
        {
            Min = new[] { minX, 2d, 3d },
            Max = new[] { 4d, 5d, 6d },
        },
    };

    static SideDatabaseMetadata Translated(double x, double y, double z) => new()
    {
        EntityCount = 2,
        EntityTypes = new Dictionary<string, int> { ["Line"] = 2 },
        Extents = new SideDatabaseExtents
        {
            Min = new[] { 1d + x, 2d + y, 3d + z },
            Max = new[] { 4d + x, 5d + y, 6d + z },
        },
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 保存先が未設定なら無効(string? value)
    {
        Assert.Null(SideDatabaseDump.NormalizeStem(value));
    }

    [Fact]
    public void 保存先の語幹から観測点別のパスを作る()
    {
        Assert.Equal(@"C:\temp\side.raw.json",
            SideDatabaseDump.GetPath(@"C:\temp\side", "raw"));
        Assert.Equal(@"C:\temp\side.ready.json",
            SideDatabaseDump.GetPath(@"C:\temp\side", "ready"));
    }

    [Fact]
    public void Metadataを日本語を保持したJSONへ保存する()
    {
        using var temp = new TempFolder();
        string dir = temp.Path;
        string stem = Path.Combine(dir, "側DB");
        SideDatabaseDump.Write(stem, new SideDatabaseMetadata
        {
            Observation = "raw",
            CreationMethod = "Wblock",
            EntityCount = 2,
            EntityTypes = new Dictionary<string, int> { ["Line"] = 2 },
            Extents = new SideDatabaseExtents
            {
                Min = new[] { 1.0, 2.0, 3.0 },
                Max = new[] { 4.0, 5.0, 6.0 },
            },
            ExtentsUnavailableCount = 0,
            Layout = new Dictionary<string, string?> { ["LayoutName"] = "モデル" },
        });

        string json = File.ReadAllText(stem + ".raw.json");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("raw", root.GetProperty("observation").GetString());
        Assert.Equal("Wblock", root.GetProperty("creationMethod").GetString());
        Assert.Equal(2, root.GetProperty("entityCount").GetInt32());
        Assert.Equal("モデル", root.GetProperty("layout").GetProperty("LayoutName").GetString());
        Assert.Contains("モデル", json);
    }

    [Fact]
    public void 件数と型別件数とExtentsが同じなら側DBは一致する()
    {
        Assert.Equal(SideDatabaseSnapshotMismatch.None,
            SideDatabaseSnapshotComparer.Compare(Snapshot(), Snapshot(1 + 1e-10)));
    }

    [Fact]
    public void 件数が同じでもExtentsの平行移動を不一致にする()
    {
        Assert.Equal(SideDatabaseSnapshotMismatch.Extents,
            SideDatabaseSnapshotComparer.Compare(Snapshot(), Snapshot(100)));
    }

    [Fact]
    public void 一様な平行移動だけは元座標へ戻す変位を得られる()
    {
        Assert.True(SideDatabaseSnapshotComparer.TryGetUniformTranslation(
            Snapshot(), Translated(10, -20, 30), out double[] displacement));
        Assert.Equal(new[] { -10d, 20d, -30d }, displacement);
    }

    [Fact]
    public void 大きさ変更を平行移動として受理しない()
    {
        Assert.False(SideDatabaseSnapshotComparer.TryGetUniformTranslation(
            Snapshot(), Snapshot(100), out _));
    }

    [Fact]
    public void 型別件数とExtents取得不能をそれぞれ不一致にする()
    {
        var differentTypes = Snapshot();
        differentTypes = new SideDatabaseMetadata
        {
            EntityCount = differentTypes.EntityCount,
            EntityTypes = new Dictionary<string, int> { ["Circle"] = 2 },
            Extents = differentTypes.Extents,
        };
        Assert.Equal(SideDatabaseSnapshotMismatch.EntityTypes,
            SideDatabaseSnapshotComparer.Compare(Snapshot(), differentTypes));
        Assert.Equal(SideDatabaseSnapshotMismatch.ExtentsAvailability,
            SideDatabaseSnapshotComparer.Compare(Snapshot(), Snapshot(unavailable: 1)));
    }

    // 不一致の理由の文は、種類だけでなく元の選択と側DB の値の両方を含み、原因を確かめられること
    public static TheoryData<SideDatabaseMetadata, string, string, string> 範囲と件数の不一致 => new()
    {
        { Snapshot(minX: 1.5), "Extents", "source=(1, 2, 3)-(4, 5, 6)", "side=(1.5, 2, 3)-(4, 5, 6)" },
        { Snapshot(lineCount: 3), "EntityCount", "source=2", "side=3" },
    };

    [Theory]
    [MemberData(nameof(範囲と件数の不一致))]
    public void 範囲と件数の不一致の説明は元の選択と側DBの値を両方含む(SideDatabaseMetadata side,
        string kind, string sourceText, string sideText)
    {
        string text = SideDatabaseSnapshotComparer.Describe(Snapshot(), side);
        Assert.StartsWith(kind, text);
        Assert.Contains(sourceText, text);
        Assert.Contains(sideText, text);
    }

    [Fact]
    public void 型別件数と範囲の取得可否の不一致の説明は両方の値を含む()
    {
        var circles = new SideDatabaseMetadata
        {
            EntityCount = 2,
            EntityTypes = new Dictionary<string, int> { ["Circle"] = 2 },
            Extents = Snapshot().Extents,
        };
        string types = SideDatabaseSnapshotComparer.Describe(Snapshot(), circles);
        Assert.StartsWith("EntityTypes", types);
        Assert.Contains("source=Line:2", types);
        Assert.Contains("side=Circle:2", types);

        string availability = SideDatabaseSnapshotComparer.Describe(Snapshot(), Snapshot(unavailable: 1));
        Assert.StartsWith("ExtentsAvailability", availability);
        Assert.Contains("source=unavailable:0", availability);
        Assert.Contains("side=unavailable:1", availability);
    }

    [Fact]
    public void 一致するときの説明はNone()
    {
        Assert.Equal("None", SideDatabaseSnapshotComparer.Describe(Snapshot(), Snapshot()));
    }
}
