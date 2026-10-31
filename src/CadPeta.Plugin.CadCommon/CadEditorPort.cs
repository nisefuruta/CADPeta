using System;
using System.Collections.Generic;
using System.IO;

#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
// acmgd（AutoCAD.NET）にも同じ名前の型があるので、accoreconsole にもある Core の Application を使う
using Application = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using EditorPromptResult = Autodesk.AutoCAD.EditorInput.PromptResult;
using Autodesk.AutoCAD.Geometry;
using ProductPluginDefaults = CadPeta.Plugin.Acad.AcadPluginDefaults;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.EditorInput;
using Teigha.Geometry;
using EditorPromptResult = Teigha.EditorInput.PromptResult;
using ProductPluginDefaults = CadPeta.Plugin.Ares.AresPluginDefaults;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.EditorInput;
using EditorPromptResult = Itjp.Ijcad.EditorInput.PromptResult;
using Itjp.Ijcad.Geometry;
using ProductPluginDefaults = CadPeta.Plugin.Ijcad.IjcadPluginDefaults;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Bricscad.EditorInput;
using EditorPromptResult = Bricscad.EditorInput.PromptResult;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using ProductPluginDefaults = CadPeta.Plugin.Bricscad.BricscadPluginDefaults;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;

// 対話ポートの CAD 実装（4 製品で共有）。範囲プロンプト（Shared.RangePrompt）と出力先問い合わせ
// （Shared.OutputLocation）が必要とする対話プリミティブと図面属性を各製品の SDK で提供する。
// 窓 pick のペーパー空間切替はこの実装の内側で完結し、中立矩形だけを返す。
// 窓の座標系の規則そのものは Shared.WindowPickMapper が唯一の情報源で、ここは SDK の値を
// 中立型へ移して渡すだけにする（4製品で規則を複製しない）。製品ごとの違いは目標点の読み方
// （ReadPickFrame）だけで、#if で分ける。
internal sealed class CadEditorPort : IEditorPort
{
    readonly Document _doc;
    readonly Editor _ed;

    public CadEditorPort(Document doc)
    {
        _doc = doc;
        _ed = doc.Editor;
    }

    // DWGTITLED=0 は未保存図面（doc.Name がパスを持たない）
    public bool IsDrawingSaved =>
        Convert.ToInt32(Application.GetSystemVariable("DWGTITLED")) != 0;

    public string DrawingPath => _doc.Name;

    public string DrawingStem => Path.GetFileNameWithoutExtension(_doc.Name);

    public string CurrentLayoutName => LayoutManager.Current.CurrentLayout;

    public object? GetPickfirstSelection() => SelectedIds(_ed.SelectImplied(), "GetPickfirstSelection.SelectImplied");

    /// <summary>選択の結果を試験の記録（PromptTrace）に書き、1 件以上選ばれていればその ID を返す（無ければ null）。</summary>
    static ObjectId[]? SelectedIds(PromptSelectionResult result, string traceName)
    {
        var value = result.Value;
        PromptTrace.Write(traceName, result.Status.ToString(), null, valueIsNull: value == null, count: value?.Count);
        return result.Status == PromptStatus.OK && value != null && value.Count > 0 ? value.GetObjectIds() : null;
    }

    public SelectionResult PromptSelection()
    {
        // O は先選択の再採用ではなく、利用者が明示した後選択への切替である。
        // SelectImplied の集合が残ったまま GetSelection すると即時確定するため、先に解除する。
        _ed.SetImpliedSelection(Array.Empty<ObjectId>());
        var sel = _ed.GetSelection();
        if (SelectedIds(sel, "PromptSelection.GetSelection") is { } ids)
            return SelectionResult.OfSelected(ids);
        if (sel.Status == PromptStatus.Error || sel.Status == PromptStatus.None)
            return SelectionResult.OfEmpty();
        return SelectionResult.OfCancelled();
    }

    public string? PromptString(string message)
    {
        var prompt = new PromptStringOptions(message) { AllowSpaces = true };
        var answer = _ed.GetString(prompt);
        return answer.Status == PromptStatus.OK ? answer.StringResult : null;
    }

    public bool HasScreen => PetaCommand.HasScreen;

    // 画面の試験は .Waiting の行を見てからダイアログを探し、結果の行（OK なら入力したコメント）で判定する
    public bool ShowCommentDialog(CommentForm form)
    {
        PromptTrace.Write("ShowCommentDialog.Waiting", "Waiting", null, valueIsNull: true, count: null);
        bool ok = GuiCommands.ShowCommentDialog(form);
        PromptTrace.Write("ShowCommentDialog", ok ? "OK" : "Cancel", ok ? form.Comment : null, valueIsNull: !ok, count: null);
        return ok;
    }

    public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
                                             double defaultValue, bool useDefault,
                                             bool allowNone, bool allowZero, bool allowNegative)
    {
        var options = new PromptDoubleOptions(message)
        {
            AllowNone = allowNone,
            AllowNegative = allowNegative,
            AllowZero = allowZero,
            AppendKeywordsToMessage = false, // 文言にキーワードを自書きしているため（GUI 実機で確認済みの流儀）
            UseDefaultValue = useDefault,
            DefaultValue = defaultValue,
        };
        var result = PromptWithKeywords(options, keywords, () => _ed.GetDouble(options), "PromptDouble.GetDouble");
        return result.Status switch
        {
            PromptStatus.None => PromptResult<double>.OfNone(),
            PromptStatus.OK => PromptResult<double>.OfValue(result.Value),
            PromptStatus.Keyword => PromptResult<double>.OfKeyword(
                PromptKeywordResolver.ResolveGlobal(result.StringResult, keywords)),
            _ => PromptResult<double>.OfCancelled(),
        };
    }

    public PromptResult<string> PromptKeywords(string message, IReadOnlyList<PromptKeyword> keywords)
    {
        var options = new PromptKeywordOptions(message)
        {
            AllowNone = true, // Enter は呼び出し元の既定（-PETAOPT は保存して終了）
            AppendKeywordsToMessage = false, // 文言にキーワードを自書きしているため
        };
        // キーワードだけの入力は、選んだキーワードを OK で返す（ほかの入力は Keyword で返す）
        var result = PromptWithKeywords(options, keywords, () => _ed.GetKeywords(options), "PromptKeywords.GetKeywords", PromptStatus.OK);
        return result.Status switch
        {
            PromptStatus.None => PromptResult<string>.OfNone(),
            PromptStatus.OK => PromptResult<string>.OfKeyword(
                PromptKeywordResolver.ResolveGlobal(result.StringResult, keywords)),
            _ => PromptResult<string>.OfCancelled(),
        };
    }

    /// <summary>
    /// キーワードを足した入力を行い、結果を試験の記録（PromptTrace）に書いて返す。キーワードを選んだとき（<paramref name="keywordStatus"/>）は
    /// その文字列も書く。入力を待つ直前にも 1 行書く（画面の試験は、この行を見てからコマンド ラインへ答えを送る。
    /// 待つ前に送った文字列は問い合わせの答えにならなかった。AutoCAD 2027 で、範囲の問い合わせが取り消しで終わり、後で PETA がもう一度始まった）。
    /// </summary>
    static TResult PromptWithKeywords<TResult>(PromptOptions options, IReadOnlyList<PromptKeyword> keywords,
                                               Func<TResult> prompt, string traceName,
                                               PromptStatus keywordStatus = PromptStatus.Keyword) where TResult : EditorPromptResult
    {
        foreach (var kw in keywords)
            options.Keywords.Add(kw.Global, kw.Local, kw.Display);
        PromptTrace.Write(traceName + ".Waiting", "Waiting", null, valueIsNull: true, count: null);
        var result = prompt();
        PromptTrace.Write(traceName, result.Status.ToString(),
            result.Status == keywordStatus ? result.StringResult : null,
            valueIsNull: false, count: null);
        return result;
    }

    public void WriteLine(string message) => _ed.WriteMessage("\n" + message);

    public IDisposable BeginWindowPickScope(TextWriter verbose)
    {
        // レイアウトタブでビューポート内（MSPACE）に居る場合、pick はモデル座標になり
        // プロット窓（ペーパー座標＝DCS）と一致しない。PLOT ダイアログの窓指定と同様に
        // ペーパー空間へ切り替えて pick し、終了後に元のビューポート操作状態へ戻す
        short tileMode = Convert.ToInt16(Application.GetSystemVariable("TILEMODE"));
        short activeViewport = Convert.ToInt16(Application.GetSystemVariable("CVPORT"));
        return WindowPickSpaceScope.Enter(tileMode, activeViewport,
            ProductPluginDefaults.PaperSpaceViewportNumber,
            _ed.SwitchToPaperSpace,
            () =>
            {
                _ed.SwitchToModelSpace();
                short restored = Convert.ToInt16(Application.GetSystemVariable("CVPORT"));
                if (restored != activeViewport)
                    throw new InvalidOperationException(
                        Msg.Format("editor.cvport_restore_mismatch", activeViewport, restored));
            },
            () => verbose.WriteLine(Msg.Text("editor.paper_pick_switch")));
    }

    public PromptResult<PlotWindow> PromptWindow(string firstMessage,
                                                 IReadOnlyList<PromptKeyword> keywords,
                                                 string secondMessage)
    {
        var firstOptions = new PromptPointOptions(firstMessage)
        {
            AllowNone = true, // Enter=既定（全体）
            // 文言にキーワードを自書きしているため自動付加を止める
            //（既定 true のままだと「… <全体(A)> [全体(A)/余白(M)]:」と二重表示になる。GUI 実機で確認）
            AppendKeywordsToMessage = false,
        };
        var first = PromptWithKeywords(firstOptions, keywords, () => _ed.GetPoint(firstOptions), "PromptWindow.GetPoint");

        if (first.Status == PromptStatus.None)
            return PromptResult<PlotWindow>.OfNone();
        if (first.Status == PromptStatus.Keyword)
            return PromptResult<PlotWindow>.OfKeyword(
                PromptKeywordResolver.ResolveGlobal(first.StringResult, keywords));
        if (first.Status != PromptStatus.OK)
            return PromptResult<PlotWindow>.OfCancelled();

        var second = _ed.GetCorner(new PromptCornerOptions(secondMessage, first.Value));
        if (second.Status != PromptStatus.OK)
            return PromptResult<PlotWindow>.OfCancelled();

        // 座標系のフレームは2点を pick し終えてから1回だけ読む。DCS は視線の向き（Target・
        // ViewDirection・ViewTwist）で決まり、ズーム・パンでは変わらない（実測確認済み）。
        // 2点を同じフレームで換算するため、pick の途中で表示を変えても窓は壊れない。
        var result = WindowPickMapper.ToPlotWindow(
            ToVec3(first.Value), ToVec3(second.Value), ReadPickFrame(), ProductPluginDefaults.WindowPickContract);
        if (result.Outcome == WindowPickOutcome.ViewPlaneDegenerate)
        {
            // UCS 平面を真横から見ており窓が線に潰れる。CAD 自身も窓プロットを中止する状態のため、
            // 誤った成果物を出さずに理由を示して止める
            WriteLine(Msg.Text("editor.view_plane_degenerate"));
            return PromptResult<PlotWindow>.OfCancelled();
        }
        return PromptResult<PlotWindow>.OfValue(result.Window);
    }

    /// <summary>
    /// 窓 pick 時点の座標系を中立型で読む。
    /// AutoCAD: ViewTableRecord の Target・ViewDirection は WCS
    /// （システム変数 TARGET は UCS で別物。取り違えると変換が壊れる。実測確認済み）。
    /// ARES: 目標点は GetCurrentView().Target でなくシステム変数 TARGET から読む。ARES 2027 の
    /// GetCurrentView().Target はビュー中心を畳み込んだ値を返し、真の目標点にならない（2026-07-17 実測:
    /// 設定 (60,50)・中心 (40,25)・ねじれ 36.87° で (107,46) が返る。2024 は設定値のまま）。この汚染された
    /// 目標点で DCS を組むと窓が別領域になり、「DCS を送ると空になる」（2026-07-13 観測）を再現する。
    /// ARES のシステム変数 TARGET は両版とも WCS の真値を返す（AutoCAD 系の UCS 表現と違う。
    /// 回転 UCS の計測ケース G で UCS 表現 (6.6,113.8) でなく WCS (60,50) を実測。
    /// tests/tools/regression/window_contract の window_contract_compat.ps1 で確定）。
    /// ViewDirection・ViewTwist は両版とも設定値どおりを返す（同計測）。
    /// IJCAD・BricsCAD: ViewTableRecord の Target・ViewDirection は AutoCAD 公式仕様では WCS だが、
    /// これらの製品での座標系は未実測である（tests/tools/regression/window_contract を実行して確定させる）。
    /// </summary>
    WindowPickFrame ReadPickFrame()
    {
        var cs = _ed.CurrentUserCoordinateSystem.CoordinateSystem3d;
        using var view = _ed.GetCurrentView();
#if CADPETA_ARES
        var target = (Point3d)Application.GetSystemVariable("TARGET");
#else
        var target = view.Target;
#endif
        return new WindowPickFrame(
            ToVec3(cs.Origin), ToVec3(cs.Xaxis), ToVec3(cs.Yaxis), ToVec3(cs.Zaxis),
            ToVec3(target), ToVec3(view.ViewDirection), view.ViewTwist);
    }

    static Vec3 ToVec3(Point3d p) => new(p.X, p.Y, p.Z);
    static Vec3 ToVec3(Vector3d v) => new(v.X, v.Y, v.Z);

}
