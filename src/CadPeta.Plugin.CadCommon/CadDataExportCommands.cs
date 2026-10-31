using System.Collections.Generic;
using System.IO;

#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using CadIrAcquirer = CadPeta.Acquire.Acad.CadIrAcquirer;
using CadTransaction = CadPeta.Acquire.Acad.CadTransaction;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using CadIrAcquirer = CadPeta.Acquire.Ares.CadIrAcquirer;
using CadTransaction = CadPeta.Acquire.Ares.CadTransaction;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.Runtime;
using CadIrAcquirer = CadPeta.Acquire.Ijcad.CadIrAcquirer;
using CadTransaction = CadPeta.Acquire.Ijcad.CadTransaction;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.Runtime;
using CadIrAcquirer = CadPeta.Acquire.Bricscad.CadIrAcquirer;
using CadTransaction = CadPeta.Acquire.Bricscad.CadTransaction;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using System.Diagnostics;
using CadPeta.Acquire.Common;
using CadPeta.Ir;
using CadPeta.Out.Emf;
using CadPeta.Plugin.Shared;

// PETADATA/PETADATADIAG（構造SVG書き出し、DB直接取得）の共有結線。4製品の PetaCommand.cs は
// EditorPort/IrAcquirer の型名以外は同文だったため、CadSelectionPlotPort.cs と同じ方式
// （#if 分岐＋using エイリアス＋csproj の Compile Include リンク）でここへ1本化する。
// PetaCommand は各製品ファイルとこのファイルの partial として構成し、Describe（例外種別ごとの
// 説明文。製品ごとにメッセージ・例外型が異なる）だけは各製品ファイル側に残す。
public static partial class PetaCommand
{
    /// <summary>PETADATADIAG のコマンド名。PETADATA と同じ結線に、SvgExportRunner の
    /// 段階別診断（diagnostic:true）を加える。既定コマンド PETADATA は診断オフ
    /// （PETA/PETADIAG の関係と同形）。</summary>
    const string DataDiagCommandName = "PETADATADIAG";

    [CommandMethod(PluginDefaults.DataCommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void ExecuteData() => InvokeData(diagnostic: false);

    // 診断モード。構造SVG書き出し経路の段階別ダンプを加える（PETADIAG が
    // PetaRunner.Run(..., diagnostic: true) を結線するのと同じ形）。
    [CommandMethod(DataDiagCommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void ExecuteDataDiag() => InvokeData(diagnostic: true);

    // コマンドの境界（図面の確認・言語の設定・例外の報告）は PetaCommand.cs の RunAtCommandBoundary が PETA 系と共通に持つ
    static void InvokeData(bool diagnostic) =>
        RunAtCommandBoundary(diagnostic ? DataDiagCommandName : PluginDefaults.DataCommandName, (doc, log) =>
            SvgExportRunner.Run(BuildSvgEnvironment(doc, log, log), log, log, diagnostic));

    static SvgExportEnvironment BuildSvgEnvironment(Document doc, TextWriter log, TextWriter verbose)
    {
        var db = doc.Database;
        return new SvgExportEnvironment
        {
            Editor = new CadEditorPort(doc),
            TrueTypeOutlinerFactory = () => new GdiGlyphOutliner(),
            AcquireSelection = (handle, choice, diag) =>
            {
                var ids = (ObjectId[])handle;
                var sw = Stopwatch.StartNew();
                IrDocument ir;
                int opened;
                using (var tr = new CadTransaction(db))
                {
                    var spaceIds = FilterCurrentSpaceIds(tr, db, ids, diag, log);
                    if (spaceIds.Count == 0)
                    {
                        // 全件除外: 側で写像すべきものが残らないため、空文書を返して
                        // SvgExportRunner 側の中止メッセージ・診断サマリへ委ねる
                        tr.Commit();
                        return new IrDocument();
                    }
                    ir = new CadIrAcquirer(log, verbose).Acquire(tr, db, spaceIds,
                                                 choice.ScaleDenominator, choice.MarginMm, diag,
                                                 choice.ShxSubstitute, choice.GradientDpi);
                    opened = tr.OpenedCount;
                    tr.Commit(); // 読み取り専用トランザクションの正常終了
                }
                verbose.WriteLine(Msg.Format("data.db_acquired", ids.Length, sw.ElapsedMilliseconds, opened));
                return ir;
            },
        };
    }

    /// <summary>
    /// 現在の空間（モデル空間またはカレントレイアウトのペーパー空間）直属でない
    /// 選択オブジェクトを除外する。選択印刷（CadSelectionPlotPort.cs の OtherSpace 検査）と
    /// 同じ規則。PETADATA は従来この検査がなく、他空間の図形を無警告で SVG 化していた。
    /// 除外した各件は診断へ記録し、1件以上あれば行動可能なログを1行出す。
    /// 全件除外時は呼び出し側が空の IrDocument を返す。
    /// </summary>
    static List<ObjectId> FilterCurrentSpaceIds(CadTransaction tr, Database db, ObjectId[] ids,
        IrDiagnostics diag, TextWriter log)
    {
        var currentSpaceId = db.CurrentSpaceId;
        var kept = new List<ObjectId>(ids.Length);
        int excluded = 0;
        foreach (var id in ids)
        {
            // 非図形オブジェクト（ent==null）の扱いは取得器（CadIrAcquirer.Acquire）の
            // 既存挙動（非図形スキップの診断＋続行）へそのまま委ね、ここでは除外しない
            var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
            if (ent != null && ent.OwnerId != currentSpaceId)
            {
                diag.Add(AcquireDiagCategories.SelectionOtherSpace, id.Handle.ToString());
                excluded++;
                continue;
            }
            kept.Add(id);
        }
        if (excluded > 0)
            log.WriteLine(Msg.Format("data.other_space_excluded", excluded, ids.Length));
        return kept;
    }
}
