using System;
using System.Collections.Generic;
using System.Globalization;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// リボンのパネルの置き方（作者の製品のシリーズの取り決め。ほかの製品も同じ Id の形と並べ方で置く）。リボンの型を使わない計算にして、
/// 単体試験で固定する。パネルの Id は <c>WKAZUFUMI.Panel.</c> + 4 桁の並び順の番号 + <c>.</c> + 製品名で、番号の小さい順に左から並ぶ。
/// </summary>
public static class RibbonLayout
{
    const string PanelPrefix = "WKAZUFUMI.Panel.";

    /// <summary>CADPeta の並び順の番号（利用者の決定。シリーズの番号の表は autocad-plugin-dev の references/ui.md）。</summary>
    public const int SeriesOrder = 30;

    /// <summary>標準の「アドイン」タブの CUI の要素 Id（acad.cuix）。実行時の Id はメニューグループ付き（<c>ACAD.</c>）。</summary>
    public const string AddinsTabElementId = "RBN_00012112";

    /// <summary>「アドイン」タブが無いときに作るタブ。作者の製品で共通（ほかの製品もこの Id を探す）。</summary>
    public const string FallbackTabId = "WKAZUFUMI.AddinsTab";

    /// <summary>CADPeta のパネルの Id。</summary>
    public static string PanelId { get; } =
        PanelPrefix + SeriesOrder.ToString("D4", CultureInfo.InvariantCulture) + ".CADPeta";

    /// <summary>標準の「アドイン」タブの Id か（要素 Id そのもの、またはメニューグループ付き）。</summary>
    public static bool IsAddinsTab(string? tabId) =>
        tabId != null && (tabId == AddinsTabElementId || tabId.EndsWith("." + AddinsTabElementId, StringComparison.Ordinal));

    /// <summary>
    /// CADPeta のパネルを入れる位置。番号が大きいシリーズのパネルの直前、無ければ小さいパネルの直後、シリーズのパネルが無ければ左端（0）。
    /// シリーズ以外のパネル（Id が null のものを含む）は数えるだけで比べない。
    /// </summary>
    public static int InsertIndex(IReadOnlyList<string?> panelIds)
    {
        int afterLower = -1;
        for (int index = 0; index < panelIds.Count; index++)
        {
            int? other = OrderOf(panelIds[index]);
            if (other == null) continue;
            if (other > SeriesOrder) return index;
            afterLower = index + 1;
        }
        return afterLower >= 0 ? afterLower : 0;
    }

    /// <summary>
    /// リボンのボタンが送る文字列。組み込みの別名と取り違えないよう _. を付ける。コマンドの実行中だけ、標準のボタンのマクロ（^C^C）と
    /// 同じく取り消しの文字（Ctrl+C の文字コード 3 を 2 つ）を先に送る。実行中でないときに送ると、先に選んだオブジェクトの選択が
    /// 解除され、PETA の絞り込みに使えない。
    /// </summary>
    public static string ButtonCommand(string command, bool commandActive) =>
        (commandActive ? new string((char)3, 2) : "") + "_." + command + " ";

    /// <summary>シリーズのパネルの Id なら並び順の番号、そうでなければ null。</summary>
    public static int? OrderOf(string? panelId)
    {
        if (panelId == null || !panelId.StartsWith(PanelPrefix, StringComparison.Ordinal)) return null;
        string digits = panelId.Substring(PanelPrefix.Length).Split('.')[0];
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out int order) ? order : null;
    }
}