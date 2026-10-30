// 文字アウトライン化（設定オプション）: IR の text 要素をグリフ輪郭の塗り path へ置換する。
// フォント保持は貼り付け先での編集性に優れ、輪郭化は環境非依存の見た目を保証する——
// この二方式の後者を選んだ場合の変換段で、文字編集性を失う非可逆処理として
// 件数と対象を処理ログへ記録する。置換は同位置の 1:1（Nodes の数と描画順を変えない）。
using System.Collections.Generic;
using CadPeta.Ir;

namespace CadPeta.UseCases;

public static class TextOutlineTransform
{
    public static void Apply(IrDocument ir, IGlyphOutliner outliner, IrDiagnostics diag)
    {
        OutlineList(ir.Nodes, outliner, diag);
    }

    static void OutlineList(List<IrNode> nodes, IGlyphOutliner outliner, IrDiagnostics diag)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            switch (nodes[i])
            {
                case IrGroup g:
                    OutlineList(g.Children, outliner, diag);
                    break;
                case IrText t:
                {
                    var run = GlyphRun.TrueType(t.Value, t.FontFamily, t.FontSizeMm,
                                                t.WidthFactor, t.GlyphXsMm);
                    var textSpace = outliner.Outline(run);
                    if (textSpace == null)
                    {
                        // 輪郭を取れない場合は text のまま残す（見た目を失うより情報保持を優先）
                        diag.Add("文字アウトライン化: 輪郭を取れないため text を維持",
                                 $"'{t.Value}' font={t.FontFamily}");
                        break;
                    }
                    nodes[i] = new IrPath
                    {
                        Segments = GlyphPlacement.Place(textSpace, t.XMm, t.YMm,
                                                        t.RotationDeg, t.WidthFactor),
                        // グリフ輪郭は非ゼロ巻き数で塗る（穴の輪郭は逆向きで定義される）
                        Fill = t.Fill,
                        FillOpacity = t.FillOpacity,
                        ClipId = t.ClipId,
                        DataLayer = t.DataLayer,
                        DataEntity = t.DataEntity,
                        DataColorMethod = t.DataColorMethod,
                        DataAci = t.DataAci,
                    };
                    diag.Add("文字アウトライン化: text を path へ展開（文字編集性の喪失）",
                             $"'{t.Value}' font={t.FontFamily}");
                    break;
                }
            }
        }
    }
}
