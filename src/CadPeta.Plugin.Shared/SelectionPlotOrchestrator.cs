using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadPeta.Acquire.Common;

namespace CadPeta.Plugin.Shared;

// 選択絞り込みの開始判断（4製品共通の単一実装）: 製品ポートの限定結果を検査し、対象なしの中止と
// 非対応図形（Xref・制御/オフビューポート・他空間）の除外確認だけを担う。範囲・尺度・クロップの判断は
// 通常経路と同じ PlotOrchestrator が session.Plotter に対して行う。
public static class SelectionPlotOrchestrator
{
    /// <summary>
    /// 選択絞り込みセッションを開始し、非対応図形の除外確認まで済ませる。null は中止
    /// （対象なし・利用者の不同意）。返したセッションの後始末は呼び出し側が担う。
    /// </summary>
    public static ISelectionPlotSession? Begin(ISelectionPlotPort port, IEditorPort editor,
        object selectionHandle, TextWriter log, TextWriter verbose)
    {
        var session = port.Begin(selectionHandle, log, verbose);
        try
        {
            if (session.SupportedCount == 0)
            {
                string details = session.Unsupported.Count == 0 ? ""
                    : Msg.Format("selplot.none_supported_detail", FormatUnsupported(session.Unsupported));
                log.WriteLine(Msg.Format("selplot.none_supported", details));
                session.Dispose();
                return null;
            }
            if (session.Unsupported.Count > 0)
            {
                log.WriteLine(Msg.Format("selplot.excluded", FormatUnsupported(session.Unsupported)));
                var summary = new UnsupportedSelectionSummary(session.SelectedCount, session.Unsupported);
                if (!SelectionConfirmPrompts.ConfirmPartialSelection(editor, summary))
                {
                    log.WriteLine(Msg.Text("selplot.abort_unsupported"));
                    session.Dispose();
                    return null;
                }
            }
            // 絞り込みの確定サマリは、除外確認まで済んだあとに一度だけ出す
            log.WriteLine(Msg.Format("selplot.session_summary",
                session.SelectedCount, session.SupportedCount));
            return session;
        }
        catch (Exception original)
        {
            CleanupRunner.RunAfterFailure(original, session.Dispose);
            throw;
        }
    }

    static string FormatUnsupported(IReadOnlyList<UnsupportedSelectionItem> unsupported) =>
        string.Join(Msg.Text("selplot.separator"),
            unsupported.Select(x => $"{x.Kind} {x.Identifier}: {x.Reason}"));
}
