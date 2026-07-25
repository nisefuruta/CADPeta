// 変換層のレイヤ別グループ化: data-layer をキーに要素をレイヤ初出順へ再配列し、
// グループ境界（IrLayerGroup）を設定する。グループ内は元の相対順を保存する。
// g 要素への直列化は SvgWriter が担う。
// 再配列は z順（描画順）を変えるため、レイヤが交錯する実物図面では見た目が崩れ得る
// （実測済み）。そのため既定オフの明示オプションとし、適用時は元へ戻せない処理として
// レイヤ数と移動要素数を診断ログへ記録する。
using CadPeta.Ir;

namespace CadPeta.Transforms;

public static class LayerGroupTransform
{
    /// <summary>data-layer を持たない要素（レイヤ情報なしのPDF印刷経路のPDF等）のグループ標示。</summary>
    public const string UnnamedLayerLabel = "(レイヤなし)";

    /// <summary>再グループを適用し、移動要素数を返す。
    /// 移動要素数は「レイヤの初出 run（そのレイヤが文書順で最初に現れた連続区間）に属さない
    /// 要素数」と定義する。初出 run は再配列後もグループ先頭の連続列として残るため、
    /// 元の連続位置から動くのは残りの要素である。</summary>
    public static int Apply(IrDocument ir, IrDiagnostics diag)
    {
        var order = new List<string>();                       // レイヤ初出順（キーは data-layer、無レイヤは ""）
        var groups = new Dictionary<string, List<IrNode>>();
        var firstRunLen = new Dictionary<string, int>();
        var inFirstRun = new HashSet<string>();               // 初出 run が継続中のレイヤ
        string? prevKey = null;
        foreach (var n in ir.Nodes)
        {
            string key = n.DataLayer ?? "";
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<IrNode>();
                groups[key] = list;
                order.Add(key);
                firstRunLen[key] = 0;
                inFirstRun.Add(key);
            }
            if (prevKey != null && prevKey != key)
                inFirstRun.Remove(prevKey);                   // run が途切れたレイヤの初出 run は確定
            if (inFirstRun.Contains(key))
                firstRunLen[key]++;
            list.Add(n);
            prevKey = key;
        }

        int total = ir.Nodes.Count;
        int moved = total - firstRunLen.Values.Sum();

        ir.Nodes = order.SelectMany(k => groups[k]).ToList();
        ir.LayerGroups = order.Select(k => new IrLayerGroup(Label(k), groups[k].Count)).ToList();

        foreach (var key in order)
            diag.Add("レイヤ別グループ化（レイヤ初出順の再グループ）",
                     $"layer '{Label(key)}' {groups[key].Count}要素");
        diag.Add("レイヤ別グループ化（z順の再配列＝非可逆）",
                 $"レイヤ数={order.Count} 移動要素数={moved}/{total}");
        return moved;
    }

    static string Label(string key) => key.Length == 0 ? UnnamedLayerLabel : key;
}
