// PDF印刷経路用プロットポートの CAD 実装（旧 SilentPlotter の CAD 呼び出し部。4 製品で共有）。
// プロット対象レイアウト（既定は現在図面の現在レイアウト。選択絞り込み時は側DBのレイアウト）を
// PDF デバイスへダイアログなしでプロットし、PDF ファイルを生成する。
// プロットスタイル（CTB/STB）・用紙・尺度はレイアウトのページ設定を尊重し、
// デバイスだけを既知のベクタ PDF ドライバへ上書きする（既定値方針）。
// プロット種別・尺度採否・窓クロップ矩形の判断は PlotOrchestrator（Shared）が担い、
// ここは CAD API の中立オペレーション（用紙実寸取得・窓設定・発行）だけを持つ。
// 製品ごとの違い（SDK の名前空間、回転の正規化、用紙一覧が取れないときの扱い）は #if で分ける。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadPlotType = Autodesk.AutoCAD.DatabaseServices.PlotType;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Acad.AcadPluginDefaults;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.PlottingServices;
using CadApplication = Teigha.ApplicationServices.Application;
using CadPlotType = Teigha.DatabaseServices.PlotType;
using CadRuntimeException = Teigha.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Ares.AresPluginDefaults;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.PlottingServices;
using CadApplication = Itjp.Ijcad.ApplicationServices.Application;
using CadPlotType = Itjp.Ijcad.DatabaseServices.PlotType;
using CadRuntimeException = Itjp.Ijcad.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Ijcad.IjcadPluginDefaults;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Bricscad.PlottingServices;
using CadApplication = Bricscad.ApplicationServices.Application;
using CadPlotType = Teigha.DatabaseServices.PlotType;
using CadRuntimeException = Teigha.Runtime.Exception;
using ProductPluginDefaults = CadPeta.Plugin.Bricscad.BricscadPluginDefaults;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Plugin.Shared;

internal sealed class CadPlotPort : IPlotPort
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

    public CadPlotPort(Document doc) : this(doc, doc.Database, ObjectId.Null) { }

    /// <summary>プロット対象を任意の DB・レイアウトへ向ける（選択絞り込みの側DB用）。
    /// layoutId が Null なら現在レイアウトを対象にする。</summary>
    public CadPlotPort(Document doc, Database db, ObjectId layoutId)
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
        _savedBackgroundPlot = CadApplication.GetSystemVariable("BACKGROUNDPLOT");
        try
        {
            // setterが状態変更後に例外を返す製品差でもcatch→EndSessionで復元する。
            CadApplication.SetSystemVariable("BACKGROUNDPLOT", 0);
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
            var layoutPaperSize = _ps.PlotPaperSize; // 用紙単位に依らず mm（AutoCAD で実測確認済み）
            var layoutPaperUnits = _ps.PlotPaperUnits;
            bool layoutUseStdScale = _ps.UseStandardScale;
            var layoutStdScaleType = _ps.StdScaleType;
            var layoutCustomScale = _ps.CustomPrintScale;

            // 第3引数 null はデバイス既定用紙へのリセットを意味する
            //（AutoCAD 実測: null のままだと A3 設定が ANSI A に化けた、2026-07-07）
            try
            {
                _psv.SetPlotConfigurationName(_ps, ProductPluginDefaults.PdfDeviceName, null);
                _psv.RefreshLists(_ps);
            }
            catch (CadRuntimeException ex)
            {
                throw new InvalidOperationException(
                    Msg.Format("plotport.device_failed", ProductPluginDefaults.PdfDeviceName, ex.ErrorStatus), ex);
            }
            SelectMedia(layoutMedia, layoutPaperSize.X, layoutPaperSize.Y, log, verbose);
            RestorePaperUnitsAndScale(layoutPaperUnits, layoutUseStdScale, layoutStdScaleType,
                                      layoutCustomScale, verbose);
#if CADPETA_ARES || CADPETA_IJCAD
            NormalizePlotRotation(verbose);
#endif
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
            _psv.SetPlotType(_ps, CadPlotType.Window);
        }
        catch (CadRuntimeException ex)
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
            _psv.SetPlotType(_ps, CadPlotType.Extents);
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, StdScaleType.ScaleToFit);
            _psv.SetPlotCentered(_ps, true);
        }
        else if (_mediaFallback)
        {
            _psv.SetPlotType(_ps, CadPlotType.Extents);
            _psv.SetUseStandardScale(_ps, true);
            _psv.SetStdScaleType(_ps, StdScaleType.ScaleToFit);
            _psv.SetPlotCentered(_ps, true);
        }
        else if (_ps.PlotType == CadPlotType.Display)
        {
            // ペーパー空間で「画面表示」が残っている場合も同じ理由でレイアウト全面に固定する
            _psv.SetPlotType(_ps, CadPlotType.Layout);
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
        catch (CadRuntimeException ex)
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
    /// 用紙一覧・実寸が取れない構成（互換 CAD。ARES 2024 は GetMediaBounds が印刷可能領域 0×0 を返す実測がある）
    /// では従来どおり既定用紙のまま進み、その事実を報告する。AutoCAD では、レイアウトの "A3"（DocuWorks Printer の
    /// 名前）が AutoCAD PDF の pc3 に無く、名前で引くだけだと既定のレターへ落ちて寸法も単位も変わっていた（実測）。
    /// </summary>
    void SelectMedia(string layoutMedia, double paperWidthMm, double paperHeightMm,
                     TextWriter log, TextWriter verbose)
    {
        string? matched = null;
        bool sizeVerified = true;
        var media = new List<PaperMediaCandidate>();
        // 既定用紙へ進むとき、または用紙の寸法を確かめずに使うときに報告する理由。用紙の一覧を取れなかった例外と、
        // 寸法を取れなかった用紙は捨てずにここへ入れる
        string missingReason = "";
#if !CADPETA_ACAD
        try
        {
#endif
            var listing = EnumerateMedia();
            media = listing.Media;
            // 理由の文言は 4 つとも同じ並びの引数を受け取る（出力先、用紙の数、寸法を取れた数、寸法を取れない最初の用紙と ErrorStatus）
            missingReason = Msg.Format(PaperMediaMatcher.MissingReasonKey(listing.NameCount, media.Count),
                ProductPluginDefaults.PdfDeviceName, listing.NameCount, media.Count,
                listing.FirstSizeFailure?.Name ?? "", listing.FirstSizeFailure?.Status ?? "");
            var resolution = PaperMediaMatcher.Resolve(layoutMedia, paperWidthMm, paperHeightMm, media);
            matched = resolution.Name;
            sizeVerified = resolution.SizeVerified;
#if !CADPETA_ACAD
        }
        catch (System.Exception ex) when (ex is InvalidOperationException or NullReferenceException)
        {
            // 互換 CAD はプロッタ構成が未初期化だと用紙一覧が取れない。寸法での対応付けは諦め、理由を下で報告する
            missingReason = Msg.Format("plotport.media_list_failed", $"{ex.GetType().Name}: {ex.Message}");
        }
#endif
        if (matched != null && TrySetMedia(matched))
        {
            // 用紙の一覧が取れずにページ設定の用紙名をそのまま使えたときは、同じ名前で寸法の違う用紙かもしれないので、
            // 寸法を確かめていないことを報告する（出力寸法がページ設定と違っても、診断から原因を追えるように）
            if (!sizeVerified)
                log.WriteLine(Msg.Format("plotport.media_size_unverified", matched, missingReason));
            // 用紙の読み替えは黙って行わない（利用者が指定した用紙と成果物の関係を追えなくする）。
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
        log.WriteLine(Msg.Format("plotport.media_missing", layoutMedia, ProductPluginDefaults.PdfDeviceName, missingReason));
    }

    bool TrySetMedia(string canonicalName)
    {
        try
        {
            _psv.SetCanonicalMediaName(_ps, canonicalName);
            return true;
        }
        catch (CadRuntimeException)
        {
            return false; // 当該デバイスに無い用紙名。呼び出し側が寸法での対応付けへ進む
        }
    }

    /// <summary>
    /// 発行デバイスが持つ用紙を、正式名と公称寸法（mm）で列挙する。用紙の名前の数と、実寸を取れずに候補から外した
    /// 最初の用紙（名前と ErrorStatus）も返す（既定用紙へ進むときの理由に使う）。
    /// </summary>
    (List<PaperMediaCandidate> Media, int NameCount, (string Name, string Status)? FirstSizeFailure) EnumerateMedia()
    {
        var names = _psv.GetCanonicalMediaNameList(_ps);
        var list = new List<PaperMediaCandidate>(names.Count);
        (string Name, string Status)? firstFailure = null;
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
            catch (CadRuntimeException ex)
            {
                // 実寸を返さない用紙は寸法で対応付けられないため候補から外す
                firstFailure ??= (name, ex.ErrorStatus.ToString());
            }
        }
        return (list, names.Count, firstFailure);
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
            ProductPluginDefaults.PlotCopyCount, log, verbose);
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
            () => CadApplication.SetSystemVariable("BACKGROUNDPLOT", _savedBackgroundPlot));
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

#if CADPETA_ARES || CADPETA_IJCAD
    /// <summary>
    /// プロット回転を 0 度・90 度へ正規化する（ARES・IJCAD だけ）。両製品の Teigha 系 PDF ドライバは
    /// 180 度のとき像を用紙上で 180 度回したまま描く（IJCAD 実測 2026-07-16: PlotRotation=180 の窓プロット
    /// PDF は /Rotate 0 のまま内容が逆さまに描かれた。ARES 実測 2026-07-17: PlotRotation=180/270 の窓プロットが
    /// 回転0の像を点対称にし、クロップが平行移動では一致しなかった。0/90 は回転0と一致）。90 度は縦横が
    /// 入れ替わった用紙へ正立で描かれ、AutoCAD・BricsCAD は全回転で正立に描く。クロップは「発行 PDF の
    /// 表示ページで像が正立」を前提とするため、用紙の向きが同じで上下反転だけが違う 180→0・270→90 へ写して
    /// 発行する。出力は窓の中身の貼付画像であり、上下反転を消しても利用者の窓指定の意味は変わらない。
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
#endif

    /// <summary>
    /// プロット回転 → 用紙→表示ページの時計回り四半回転数。
    /// 90 度は縦横が入れ替わった用紙へ正立で描かれることを実機の回転コーパスで確認済み
    /// （ARES・IJCAD では、180/270 度は NormalizePlotRotation が 0/90 へ写すため、ここへは 0/90 だけが来る）。
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
        readonly CadPlotPort _port;
        bool _disposed;
        public Session(CadPlotPort port) => _port = port;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _port.EndSession();
        }
    }
}
