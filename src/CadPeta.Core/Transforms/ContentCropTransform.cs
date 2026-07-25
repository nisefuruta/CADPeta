using CadPeta.Geometry;
using CadPeta.Ir;

namespace CadPeta.Transforms;

/// <summary>背景除去後の描画要素外接矩形へページをクロップする。</summary>
public static class ContentCropTransform
{
    public static bool Apply(IrDocument ir, double marginMm, IrDiagnostics diag)
    {
        if (ir.Nodes.Count == 0)
        {
            diag.Add("内容クロップ: 描画要素なし", "IR nodes=0");
            return false;
        }

        // 各ノードの bbox はここで1回だけ求め、クロップ矩形の合併とその後の
        // CropTransform への引き渡し（可視判定用）の両方に使い回す
        // （CropTransform 側の再計算＝二重計算を避ける。ir.Nodes と同順を維持する）
        var bboxes = new double[ir.Nodes.Count][];
        for (int i = 0; i < ir.Nodes.Count; i++)
            bboxes[i] = IrBounds.NodeBbox(ir.Nodes[i]);
        var merged = IrBounds.Union(bboxes);
        if (merged[0] > merged[2])
        {
            diag.Add("内容クロップ: 有効な外接矩形なし", $"nodes={ir.Nodes.Count}");
            return false;
        }
        CropTransform.Apply(ir, new RectMm(merged[0], merged[1], merged[2], merged[3]), marginMm, diag, bboxes);
        return true;
    }
}
