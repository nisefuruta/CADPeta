// PDF印刷経路用プロットポートの IJCAD 実装（旧 SilentPlotter の CAD 呼び出し部）。
// プロット対象レイアウト（既定は現在図面の現在レイアウト。選択絞り込み時は側DBのレイアウト）を
// PDF デバイスへダイアログなしでプロットし、PDF ファイルを生成する。
// プロットスタイル（CTB/STB）・用紙・尺度はレイアウトのページ設定を尊重し、
// デバイスだけを既知のベクタ PDF ドライバへ上書きする（既定値方針）。
// プロット種別・尺度採否・窓クロップ矩形の判断は PlotOrchestrator（Shared）が担い、
// ここは CAD API の中立オペレーション（用紙実寸取得・窓設定・発行）だけを持つ。
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.PlottingServices;
using CadPeta.Geometry;
using CadPeta.Plugin.Shared;

namespace CadPeta.Plugin.Ijcad;

internal sealed class IjcadPlotPort : IPlotPort
{
    readonly Document _doc;
    readonly Database _db;
    readonly ObjectId _targetLayoutId;

    Transaction? _tr;
    bool _committed;
    PlotSettings _ps = null!;
    PlotSettingsValidator _psv = null!;
    Layout _layout = null!;
    ObjectId _layoutId;
    PlotInfo _plotInfo = null!;
    object _savedBackgroundPlot = 0;
    bool _mediaFallback;

    public IjcadPlotPort(Document doc) : this(doc, doc.Database, ObjectId.Null) { }

    /// <summary>プロット対象を任意の DB・レイアウトへ向ける（選択絞り込みの側DB用）。
    /// layoutId が Null なら現在レイアウトを対象にする。</summary>
    public IjcadPlotPort(Document doc, Database db, ObjectId layoutId)
    {
        _doc = doc;
        _db = db;
        _targetLayoutId = layoutId;
    }

    public IDisposable BeginSession(TextWriter log, TextWriter verbose)
    {
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException(
                Msg.Format("plotport.busy", PlotFactory.ProcessPlotState));

        // PlotEngine はフォアグラウンドプロット前提（バックグラウンドでは同期完了しない）。
        // 対話セッションの利用者設定を壊さないよう、実行中だけ 0 にして復元する
        _savedBackgroundPlot = Application.GetSystemVariable("BACKGROUNDPLOT");
        try
        {
            Application.SetSystemVariable("BACKGROUNDPLOT", 0);
            _tr = _db.TransactionManager.StartTransaction();
            _committed = false;
            _layoutId = _targetLayoutId.IsNull
                ? LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout)
                : _targetLayoutId;
            _layout = (Layout)_tr.GetObject(_layoutId, OpenMode.ForRead);
            verbose.WriteLine(Msg.Format("plotport.layout_info",
                _layout.LayoutName, _layout.PlotConfigurationName, _layout.CanonicalMediaName,
                string.IsNullOrEmpty(_layout.CurrentStyleSheet)
                    ? Msg.Text("plotport.style_none") : _layout.CurrentStyleSheet));

            _ps = new PlotSettings(_layout.ModelType);
            _ps.CopyFrom(_layout); // ページ設定（スタイル・尺度・領域・用紙）を流用する
            _psv = PlotSettingsValidator.Current;
            _mediaFallback = false;

            // デバイス上書きはページ設定の用紙・用紙単位・尺度をまとめて壊す。レイアウトの意図を
            // 先に控え、上書き後に復元する（AutoCAD 実測: A3・mm・1:1 のレイアウトが上書きだけで
            // レター・インチになり、1:1 が 25.4倍の意味に化けた。本製品では未実測だが同じ API 形状）
            var layoutMedia = _ps.CanonicalMediaName;
            var layoutPaperSize = _ps.PlotPaperSize;
            var layoutPaperUnits = _ps.PlotPaperUnits;
            bool layoutUseStdScale = _ps.UseStandardScale;
            var layoutStdScaleType = _ps.StdScaleType;
            var layoutCustomScale = _ps.CustomPrintScale;

            // 第3引数 null はデバイス既定用紙へのリセットを意味する
            try
            {
                _psv.SetPlotConfigurationName(_ps, IjcadPluginDefaults.PdfDeviceName, null);
                _psv.RefreshLists(_ps);
            }
            catch (Itjp.Ijcad.Runtime.Exception ex)
            {
                throw new InvalidOperationException(
                    Msg.Format("plotport.device_failed", IjcadPluginDefaults.PdfDeviceName, ex.ErrorStatus), ex);
            }
            SelectMedia(layoutMedia, layoutPaperSize.X, layoutPaperSize.Y, log, verbose);
            RestorePaperUnitsAndScale(layoutPaperUnits, layoutUseStdScale, layoutStdScaleType,
                                      layoutCustomScale, verbose);
            NormalizePlotRotation(verbose);
        }
        catch
        {
            EndSession(); // BGP 復元・トランザクション後始末
            throw;
        }
        return new Session(this);
    }

    public void SetWindow(PlotWindow window)
    {
        // 窓プロット: 窓を先に設定してから型を Window にする
        // （窓未設定のまま型だけ変えると設定が不定になるため順序を固定する）
        try
        {
            _psv.SetPlotWindowArea(_ps, new Extents2d(window.MinX, window.MinY, window.MaxX, window.MaxY));
            _psv.SetPlotType(_ps, Itjp.Ijcad.DatabaseServices.PlotType.Window);
        }
        catch (Itjp.Ijcad.Runtime.Exception ex)
        {
            throw new InvalidOperationException(
                Msg.Format("plotport.window_failed", window, ex.ErrorStatus), ex);
        }
        // 配置は中央合わせで決定的にする（クロップ矩形の算出前提。PlotWindowMapper 参照）
        _psv.SetPlotCentered(_ps, true);
    }

    public void SetWindowScale(bool useFitScale)
    {
        // フィット=用紙合わせ。フィットしない（=ページ設定の尺度で収まる）場合は何もせず尺度を維持する
        if (useFitScale || _mediaFallback)
        {
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, StdScaleType.ScaleToFit);
        }
    }

    public void SetFullPlotType()
    {
        if (_layout.ModelType)
        {
            // モデルタブはページ設定が未整備なことが多く、既定の「画面表示」領域は
            // 自動実行では不定になる。図形範囲・用紙合わせ・中央配置で決定的にする
            _psv.SetPlotType(_ps, Itjp.Ijcad.DatabaseServices.PlotType.Extents);
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, StdScaleType.ScaleToFit);
            _psv.SetPlotCentered(_ps, true);
        }
        else if (_mediaFallback)
        {
            _psv.SetPlotType(_ps, Itjp.Ijcad.DatabaseServices.PlotType.Extents);
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, StdScaleType.ScaleToFit);
            _psv.SetPlotCentered(_ps, true);
        }
        else if (_ps.PlotType == Itjp.Ijcad.DatabaseServices.PlotType.Display)
        {
            // ペーパー空間で「画面表示」が残っている場合も同じ理由でレイアウト全面に固定する
            _psv.SetPlotType(_ps, Itjp.Ijcad.DatabaseServices.PlotType.Layout);
        }
    }

    public void Validate(TextWriter log)
    {
        _plotInfo = new PlotInfo { Layout = _layoutId, OverrideSettings = _ps };
        // デバイス上書きで用紙名が新デバイスに存在しない場合に近似用紙へ整合させる。
        // 対象が現在図面と別DB（側DB）のときだけ WorkingDatabase を退避して検証する
        using var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
        Database saved = HostApplicationServices.WorkingDatabase;
        try
        {
            if (!ReferenceEquals(_db, saved))
                HostApplicationServices.WorkingDatabase = _db;
            piv.Validate(_plotInfo);
        }
        catch (Itjp.Ijcad.Runtime.Exception ex)
        {
            throw new InvalidOperationException(
                Msg.Format("plotport.validate_failed",
                    _layout.LayoutName, _ps.CanonicalMediaName, ex.ErrorStatus), ex);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = saved;
        }
    }

    /// <summary>
    /// 発行デバイスの用紙を決める。用紙の正式名はデバイス固有なので、まず名前で引き、
    /// 無ければ寸法で対応付ける（判定規則は Shared.PaperMediaMatcher が持つ）。
    /// 用紙一覧・実寸が取れない構成では従来どおり既定用紙のまま進み、その事実を報告する。
    /// </summary>
    void SelectMedia(string layoutMedia, double paperWidthMm, double paperHeightMm,
                     TextWriter log, TextWriter verbose)
    {
        string? matched = null;
        var media = new List<PaperMediaCandidate>();
        try
        {
            media = EnumerateMedia(log).ToList();
            matched = PaperMediaMatcher.Resolve(layoutMedia, paperWidthMm, paperHeightMm, media);
        }
        catch (System.Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // プロッタ構成が未初期化で用紙一覧が取れない。寸法での対応付けは諦め、事実は下で報告する
        }
        if (matched != null && TrySetMedia(matched))
        {
            // 同名同寸法で元の用紙をそのまま使えた場合は読み替えではないため報告しない
            if (!string.Equals(matched, layoutMedia, StringComparison.OrdinalIgnoreCase))
                verbose.WriteLine(Msg.Format("plotport.media_matched_by_size",
                    layoutMedia, matched, paperWidthMm, paperHeightMm));
            return;
        }
        var target = PaperFallbackPolicy.Resolve(Environment.GetEnvironmentVariable(
            PluginDefaults.FallbackPaperSizeEnvironmentVariable));
        string? fallback = PaperMediaMatcher.SelectFallback(target.Width, target.Height, media);
        if (fallback != null && TrySetMedia(fallback))
        {
            var selected = media.First(x => string.Equals(x.CanonicalName, fallback, StringComparison.Ordinal));
            _psv.SetPlotRotation(_ps, PaperFallbackPolicy.RequiresQuarterTurn(
                target, selected.PageWidthMm, selected.PageHeightMm)
                ? PlotRotation.Degrees090 : PlotRotation.Degrees000);
            _mediaFallback = true;
            log.WriteLine(Msg.Format("plotport.media_fallback", layoutMedia, paperWidthMm, paperHeightMm, fallback));
            return;
        }
        _mediaFallback = true;
        log.WriteLine(Msg.Format("plotport.media_missing", layoutMedia, IjcadPluginDefaults.PdfDeviceName));
    }

    bool TrySetMedia(string canonicalName)
    {
        try
        {
            _psv.SetCanonicalMediaName(_ps, canonicalName);
            return true;
        }
        catch (Itjp.Ijcad.Runtime.Exception)
        {
            return false; // 当該デバイスに無い用紙名。呼び出し側が寸法での対応付けへ進む
        }
    }

    /// <summary>発行デバイスが持つ用紙を、正式名と公称寸法（mm）で列挙する。</summary>
    IEnumerable<PaperMediaCandidate> EnumerateMedia(TextWriter log)
    {
        var names = _psv.GetCanonicalMediaNameList(_ps);
        var list = new List<PaperMediaCandidate>(names.Count);
        using var probe = new PlotSettings(_layout.ModelType);
        probe.CopyFrom(_ps);
        foreach (string? name in names)
        {
            if (string.IsNullOrEmpty(name)) continue;
            try
            {
                _psv.SetCanonicalMediaName(probe, name);
                var size = probe.PlotPaperSize;
                var margins = probe.PlotPaperMargins;
                list.Add(new PaperMediaCandidate(name, size.X, size.Y,
                    size.X - margins.MinPoint.X - margins.MaxPoint.X,
                    size.Y - margins.MinPoint.Y - margins.MaxPoint.Y));
            }
            catch (Itjp.Ijcad.Runtime.Exception)
            {
                // 実寸を返さない用紙は寸法で対応付けられないため候補から外す
            }
        }
        return list;
    }

    /// <summary>
    /// デバイス上書きで変わった用紙単位と尺度を、レイアウトの意図どおりへ戻す。
    /// 用紙単位を変えると尺度の値も付随して変わるため、単位→尺度の順で戻す。
    /// </summary>
    void RestorePaperUnitsAndScale(PlotPaperUnit units, bool useStdScale, StdScaleType stdScaleType,
                                   CustomScale customScale, TextWriter verbose)
    {
        if (_ps.PlotPaperUnits != units)
        {
            verbose.WriteLine(Msg.Format("plotport.paper_units_restored", _ps.PlotPaperUnits, units));
            _psv.SetPlotPaperUnits(_ps, units);
        }
        if (useStdScale)
        {
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, stdScaleType);
        }
        else
        {
            _psv.SetUseStandardScale(_ps, false);
            _psv.SetCustomPrintScale(_ps, customScale);
        }
    }

    public PaperMetrics GetPaperMetrics(TextWriter log)
    {
        var (pageW, pageH, printW, printH, printCx, printCy) = GetPaperBounds(_ps, log);
        double s = ExplicitScaleMmPerUnit(_ps, log);
        return new PaperMetrics(pageW, pageH, printW, printH, printCx, printCy,
                                s, QuarterTurnsCw(_ps.PlotRotation), _layout.ModelType);
    }

    static (double PageW, double PageH, double PrintW, double PrintH, double PrintCx, double PrintCy)
        GetPaperBounds(PlotSettings ps, TextWriter log)
    {
        var size = ps.PlotPaperSize;
        var margins = ps.PlotPaperMargins;
        double printW = size.X - margins.MinPoint.X - margins.MaxPoint.X;
        double printH = size.Y - margins.MinPoint.Y - margins.MaxPoint.Y;
        return (size.X, size.Y, printW, printH,
            margins.MinPoint.X + printW / 2, margins.MinPoint.Y + printH / 2);
    }

    public void Publish(string outputPdfPath, TextWriter log, TextWriter verbose)
    {
        CadPlotEngineRunner.Publish(_doc, _db, _plotInfo, outputPdfPath,
            IjcadPluginDefaults.PlotCopyCount, log, verbose);
        _tr!.Commit();
        _committed = true;

    }

    // セッション後始末: BACKGROUNDPLOT 復元・トランザクション解放（未コミットは破棄）
    void EndSession()
    {
        CleanupRunner.RunAll(
            () => { if (_tr != null && !_committed) _tr.Abort(); },
            () => { try { _tr?.Dispose(); } finally { _tr = null; } },
            () => { try { _plotInfo?.Dispose(); } finally { _plotInfo = null!; } },
            () => _ps?.Dispose(),
            () => Application.SetSystemVariable("BACKGROUNDPLOT", _savedBackgroundPlot));
    }

    /// <summary>
    /// ページ設定の明示尺度（用紙mm/図面単位）。用紙合わせ（フィット）と、尺度計算が
    /// 成立しないピクセル単位は NaN を返す（呼び出し側がフィットとして扱う）。
    /// </summary>
    static double ExplicitScaleMmPerUnit(PlotSettings ps, TextWriter log)
    {
        var unit = ps.PlotPaperUnits == PlotPaperUnit.Millimeters ? PaperUnitKind.Millimeters
            : ps.PlotPaperUnits == PlotPaperUnit.Inches ? PaperUnitKind.Inches : PaperUnitKind.Pixels;
        if (unit == PaperUnitKind.Pixels)
            log.WriteLine(Msg.Format("plotport.paper_units_fit", ps.PlotPaperUnits));
        var kind = ps.UseStandardScale
            ? (ps.StdScaleType == StdScaleType.ScaleToFit ? PaperScaleKind.Fit : PaperScaleKind.Standard)
            : PaperScaleKind.Custom;
        var custom = ps.CustomPrintScale;
        return PaperScaleCalculator.MmPerDrawingUnit(unit, kind, ps.StdScale,
            custom.Numerator, custom.Denominator);
    }

    /// <summary>
    /// プロット回転を 0 度・90 度へ正規化する。IJCAD の PDF ドライバは 180 度のとき像を用紙上で
    /// 180 度回したまま描く（実測 2026-07-16: PlotRotation=180 の窓プロット PDF は /Rotate 0 の
    /// まま内容が逆さまに描かれた。90/270 度は縦横が入れ替わった用紙へ正立で描かれ、
    /// AutoCAD・BricsCAD は全回転で正立に描く）。クロップは「発行 PDF の表示ページで像が正立」を
    /// 前提とするため、用紙の向きが同じで上下反転だけが違う 180→0・270→90 へ写して発行する。
    /// 出力は窓の中身の貼付画像であり、上下反転を消しても利用者の窓指定の意味は変わらない。
    /// </summary>
    void NormalizePlotRotation(TextWriter verbose)
    {
        var rotation = _ps.PlotRotation;
        var normalized = rotation switch
        {
            PlotRotation.Degrees180 => PlotRotation.Degrees000,
            PlotRotation.Degrees270 => PlotRotation.Degrees090,
            _ => rotation,
        };
        if (normalized == rotation)
            return;
        _psv.SetPlotRotation(_ps, normalized);
        verbose.WriteLine(Msg.Format("plotport.rotation_normalized", rotation, normalized));
    }

    /// <summary>
    /// プロット回転 → 用紙→表示ページの時計回り四半回転数。
    /// 90 度は縦横が入れ替わった用紙へ正立で描かれることを実機の回転コーパスで確認済み
    /// （180/270 度は NormalizePlotRotation が 0/90 へ写すため、ここへは 0/90 だけが来る）。
    /// </summary>
    static int QuarterTurnsCw(PlotRotation rotation) => rotation switch
    {
        PlotRotation.Degrees000 => 0,
        PlotRotation.Degrees090 => 1,
        PlotRotation.Degrees180 => 2,
        PlotRotation.Degrees270 => 3,
        _ => throw new NotSupportedException($"未知のプロット回転: {rotation}"),
    };

    sealed class Session : IDisposable
    {
        readonly IjcadPlotPort _port;
        bool _disposed;
        public Session(IjcadPlotPort port) => _port = port;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _port.EndSession();
        }
    }
}
