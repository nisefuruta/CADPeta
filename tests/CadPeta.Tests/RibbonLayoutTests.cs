using System.Collections.Generic;
using System.Linq;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

// リボンのパネルの置き方（作者の製品のシリーズの取り決め）。パネルの Id は WKAZUFUMI.Panel.<4 桁の番号>.<製品名> で、CADPeta は 0030。
// 番号が大きいシリーズのパネルの直前、無ければ小さいパネルの直後、シリーズのパネルが無ければ左端に入れる。
// 置く先は標準の「アドイン」タブ（RBN_00012112。実行時はメニューグループ付き）
public sealed class RibbonLayoutTests
{
    static readonly int[] Others = { 10, 20, 50, 60, 70, 80 };

    static string Id(int order) => "WKAZUFUMI.Panel." + order.ToString("D4") + ".Other";

    [Fact]
    public void CADPetaのパネルのIdは0030()
    {
        Assert.Equal("WKAZUFUMI.Panel.0030.CADPeta", RibbonLayout.PanelId);
    }

    [Fact]
    public void パネルが無ければ左端()
    {
        Assert.Equal(0, RibbonLayout.InsertIndex(new string?[0]));
    }

    [Fact]
    public void シリーズのパネルが無ければほかのパネルの左端()
    {
        Assert.Equal(0, RibbonLayout.InsertIndex(new string?[] { "ACAD.ID_PanelA", null, "Other.Panel" }));
    }

    // シリーズのほかの製品のパネルの、あり得るすべての組み合わせ（番号の順に並び、間にシリーズ以外のパネルがある）で、
    // 入れた後のシリーズのパネルが番号の順になる
    public static IEnumerable<object[]> AllSubsets() =>
        Enumerable.Range(0, 1 << Others.Length).Select(mask => new object[] { mask });

    [Theory]
    [MemberData(nameof(AllSubsets))]
    public void 入れた後のシリーズのパネルは番号の順(int mask)
    {
        var panels = Others.Where((_, i) => (mask & (1 << i)) != 0)
            .SelectMany(order => new string?[] { "ACAD.Before" + order, Id(order) })
            .Append("ACAD.Last").ToList();

        panels.Insert(RibbonLayout.InsertIndex(panels), RibbonLayout.PanelId);

        var orders = panels.Select(RibbonLayout.OrderOf).OfType<int>().ToList();
        Assert.Equal(orders.OrderBy(o => o), orders);
    }

    [Fact]
    public void 小さい番号のパネルだけならその直後()
    {
        var panels = new string?[] { Id(10), "ACAD.A", Id(20), "ACAD.B" };

        Assert.Equal(3, RibbonLayout.InsertIndex(panels));
    }

    [Theory]
    [InlineData("RBN_00012112", true)]
    [InlineData("ACAD.RBN_00012112", true)]
    [InlineData("ACAD.RBN_000121120", false)]
    [InlineData("XRBN_00012112", false)]
    [InlineData(null, false)]
    public void アドインのタブはメニューグループ付きのIdも同じタブ(string? id, bool expected)
    {
        Assert.Equal(expected, RibbonLayout.IsAddinsTab(id));
    }
}