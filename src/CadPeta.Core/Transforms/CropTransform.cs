// 変換層2段目: クロップ。
// viewBox と物理寸法を切り出し範囲（余白込み）へ更新し、範囲外ノードを間引く。
//
// - viewBox の差し替えが切り出しの本体（ルート svg はビューポート外を描画しない。
//   実物図面で実測）
// - 範囲外ノードの間引きは保守的 bbox
//   （IrBounds: ストローク幅・文字クアッド込み）が範囲と重ならないノードだけを落とすため
//   非可逆性はないが、切り分けの助けとして件数と対象を診断ログに記録する
// - 境界と交差するノードは全体を保持する（SVG は viewBox が視覚的に切り、
//   EMF は出力側の事前クリップが切断する）
using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.Ir;

namespace CadPeta.Transforms;

public static class CropTransform
{
    /// <summary>rect（mm、ページ左上原点）へ余白 marginMm を付けてクロップし、間引き数を返す。</summary>
    public static int Apply(IrDocument ir, RectMm rect, double marginMm, IrDiagnostics diag)
        => Apply(ir, rect, marginMm, diag, precomputedBboxes: null);

    /// <summary>
    /// 内部向けオーバーロード。呼び出し側が ir.Nodes と同順で NodeBbox を求め済みなら
    /// precomputedBboxes へ渡すことで、本メソッド内での再計算（グループ再帰込み）を省ける。
    /// ContentCropTransform がクロップ矩形算出時の1周目の結果をここへ渡す用途。
    /// null なら従来どおり本メソッド内で NodeBbox を計算する。
    /// 可視判定・間引き・viewBox/寸法更新・ClipDefs 整理の挙動は precomputedBboxes の有無で変わらない。
    /// </summary>
    internal static int Apply(IrDocument ir, RectMm rect, double marginMm, IrDiagnostics diag,
                              IReadOnlyList<double[]>? precomputedBboxes)
    {
        var view = new RectMm(rect.X0 - marginMm, rect.Y0 - marginMm,
                              rect.X1 + marginMm, rect.Y1 + marginMm);
        if (view.X1 - view.X0 <= 0 || view.Y1 - view.Y0 <= 0)
            throw new ArgumentException($"クロップ範囲が空: {view}");

        // クリップ定義の bbox（クリップ範囲が切り出し範囲と重ならないノードは不可視）
        var clipBbox = ir.ClipDefs.ToDictionary(c => c.Id, c => GeoClip.Bbox(c.Segments));

        int dropped = 0;
        var kept = new List<IrNode>(ir.Nodes.Count);
        int seq = -1;
        foreach (var node in ir.Nodes)
        {
            seq++;
            var bbox = precomputedBboxes != null ? precomputedBboxes[seq] : IrBounds.NodeBbox(node);
            bool visible = view.OverlapsBbox(bbox)
                && (node.ClipId == null
                    || !clipBbox.TryGetValue(node.ClipId, out var cb)
                    || view.OverlapsBbox(cb));
            if (visible)
            {
                kept.Add(node);
                continue;
            }
            diag.Add("クロップ: 範囲外ノードの間引き",
                     $"{Kind(node)} seq={seq} {GeoClip.BboxText(bbox)}");
            dropped++;
        }
        ir.Nodes = kept;

        // 参照されなくなったクリップ定義を除去する（不可視の整理、非可逆性なし）
        // ToHashSet() は netstandard2.0 に無いためコンストラクタで等価に書く
        var referenced = new HashSet<string>(ir.Nodes.Where(n => n.ClipId != null).Select(n => n.ClipId!));
        ir.ClipDefs.RemoveAll(c => !referenced.Contains(c.Id));

        ir.ViewMinXMm = view.X0;
        ir.ViewMinYMm = view.Y0;
        ir.WidthMm = view.X1 - view.X0;
        ir.HeightMm = view.Y1 - view.Y0;
        return dropped;
    }

    static string Kind(IrNode n) => n switch
    {
        IrPath => "path",
        IrText t => $"text '{t.Value}'",
        IrImage => "image",
        IrGroup g => $"g ({g.Children.Count}子要素)",
        _ => n.GetType().Name,
    };
}
