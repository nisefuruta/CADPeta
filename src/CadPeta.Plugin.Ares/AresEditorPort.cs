// 対話ポートの ARES 実装。範囲プロンプト（Shared.RangePrompt）と出力先問い合わせ
// （Shared.OutputLocation）が必要とする対話プリミティブと図面属性を ARES SDK で提供する。
// 窓 pick のペーパー空間切替はこの実装の内側で完結し、中立矩形だけを返す。
// 窓の座標系の規則そのものは Shared.WindowPickMapper が唯一の情報源で、ここは SDK の値を
// 中立型へ移して渡すだけにする（4製品で規則を複製しない）。
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.EditorInput;
using Teigha.Geometry;
using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Ares;

internal sealed class AresEditorPort : IEditorPort
{
    readonly Document _doc;
    readonly Editor _ed;

    public AresEditorPort(Document doc)
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

    public object? GetPickfirstSelection()
    {
        var implied = _ed.SelectImplied();
        var value = implied.Value;
        PromptTrace.Write("GetPickfirstSelection.SelectImplied", implied.Status.ToString(), null,
            valueIsNull: value == null, count: value?.Count);
        if (implied.Status == PromptStatus.OK && value != null && value.Count > 0)
            return value.GetObjectIds();
        return null;
    }

    public SelectionResult PromptSelection()
    {
        // O は先選択の再採用ではなく、利用者が明示した後選択への切替である。
        // SelectImplied の集合が残ったまま GetSelection すると即時確定するため、先に解除する。
        _ed.SetImpliedSelection(Array.Empty<ObjectId>());
        var sel = _ed.GetSelection();
        var value = sel.Value;
        PromptTrace.Write("PromptSelection.GetSelection", sel.Status.ToString(), null,
            valueIsNull: value == null, count: value?.Count);
        if (sel.Status == PromptStatus.OK && value != null && value.Count > 0)
            return SelectionResult.OfSelected(value.GetObjectIds());
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
        foreach (var kw in keywords)
            options.Keywords.Add(kw.Global, kw.Local, kw.Display);
        var result = _ed.GetDouble(options);
        PromptTrace.Write("PromptDouble.GetDouble", result.Status.ToString(),
            result.Status == PromptStatus.Keyword ? result.StringResult : null,
            valueIsNull: false, count: null);
        return result.Status switch
        {
            PromptStatus.None => PromptResult<double>.OfNone(),
            PromptStatus.OK => PromptResult<double>.OfValue(result.Value),
            PromptStatus.Keyword => PromptResult<double>.OfKeyword(
                PromptKeywordResolver.ResolveGlobal(result.StringResult, keywords)),
            _ => PromptResult<double>.OfCancelled(),
        };
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
            AresPluginDefaults.PaperSpaceViewportNumber,
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
        foreach (var kw in keywords)
            firstOptions.Keywords.Add(kw.Global, kw.Local, kw.Display);
        var first = _ed.GetPoint(firstOptions);
        PromptTrace.Write("PromptWindow.GetPoint", first.Status.ToString(),
            first.Status == PromptStatus.Keyword ? first.StringResult : null,
            valueIsNull: false, count: null);

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

        // 座標系のフレームは2点を pick し終えてから1回だけ読む（2点を同じフレームで換算する）。
        // 窓の座標系の規則は Shared.WindowPickMapper が持ち、採る契約は製品ごとに実測して選ぶ。
        var result = WindowPickMapper.ToPlotWindow(
            ToVec3(first.Value), ToVec3(second.Value), ReadPickFrame(), AresPluginDefaults.WindowPickContract);
        if (result.Outcome == WindowPickOutcome.ViewPlaneDegenerate)
        {
            WriteLine(Msg.Text("editor.view_plane_degenerate"));
            return PromptResult<PlotWindow>.OfCancelled();
        }
        return PromptResult<PlotWindow>.OfValue(result.Window);
    }

    /// <summary>
    /// 窓 pick 時点の座標系を中立型で読む。目標点は GetCurrentView().Target でなく
    /// システム変数 TARGET から読む。ARES 2027 の GetCurrentView().Target はビュー中心を
    /// 畳み込んだ値を返し、真の目標点にならない（2026-07-17 実測: 設定 (60,50)・中心 (40,25)・
    /// ねじれ 36.87° で (107,46) が返る。2024 は設定値のまま）。この汚染された目標点で DCS を
    /// 組むと窓が別領域になり、「DCS を送ると空になる」（2026-07-13 観測）を再現する。
    /// ARES のシステム変数 TARGET は両版とも WCS の真値を返す（AutoCAD 系の UCS 表現と違う。
    /// 回転 UCS の計測ケース G で UCS 表現 (6.6,113.8) でなく WCS (60,50) を実測。
    /// tests/tools/regression/window_contract の window_contract_compat.ps1 で確定）。
    /// ViewDirection・ViewTwist は両版とも設定値どおりを返す（同計測）。
    /// </summary>
    WindowPickFrame ReadPickFrame()
    {
        var cs = _ed.CurrentUserCoordinateSystem.CoordinateSystem3d;
        using var view = _ed.GetCurrentView();
        var target = (Point3d)Application.GetSystemVariable("TARGET");
        return new WindowPickFrame(
            ToVec3(cs.Origin), ToVec3(cs.Xaxis), ToVec3(cs.Yaxis), ToVec3(cs.Zaxis),
            ToVec3(target), ToVec3(view.ViewDirection), view.ViewTwist);
    }

    static Vec3 ToVec3(Point3d p) => new(p.X, p.Y, p.Z);
    static Vec3 ToVec3(Vector3d v) => new(v.X, v.Y, v.Z);

}
