// 窓プロットの座標契約を実機で確定する計測器。
//
// 背景: PlotSettingsValidator.SetPlotWindowArea が期待する座標系について、Autodesk 公式仕様は DCS と
// 明記する一方、UCS が回転した実図面では現行の UCS→WCS 経路が空の結果を出す。どちらが正しいかは
// 実測で確定するほかない。本計測器は pick した2点から3候補（生値・UCS→WCS・WCS→DCS）を作り、
// 座標以外の条件を完全に揃えて別々の PDF を発行する。合わせて、判断の材料になる値を全て記録する。
//
// 設計方針:
// - 製品コードを参照しない。測定対象の実装を使って契約を測ると循環する。
// - 例外を握りつぶさない。候補ごとに失敗を記録し、1つの失敗が他を隠さないようにする。
// - コンソールの日本語出力は化けるため、記録は JSON でファイルへ書く。
using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.PlottingServices;
using Autodesk.AutoCAD.Runtime;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;

[assembly: CommandClass(typeof(CadPeta.Probe.WindowContractProbe))]

namespace CadPeta.Probe;

public sealed class WindowContractProbe
{
    /// <summary>発行先デバイス。製品コードの既定と同一にして条件を揃える。</summary>
    const string PdfDeviceName = "AutoCAD PDF (High Quality Print).pc3";

    /// <summary>候補の識別子。成果物名・記録のキーに使う唯一の情報源。</summary>
    static readonly string[] CandidateKinds = { "raw", "ucs2wcs", "wcs2dcs" };

    /// <summary>
    /// 計測条件（UCS）を厳密に設定する。UCS コマンドのプロンプト列は版・ロケール依存で脆いため、
    /// 行列を直接与えて決定的にする。原点・X方向・Y方向を環境変数で受ける。
    /// </summary>
    [CommandMethod("CPWINSETUCS", CommandFlags.Modal)]
    public void SetUcs()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            var origin = ParsePoint(RequireEnv("CPWIN_UCS_ORIGIN"));
            var xdir = ParseVector(RequireEnv("CPWIN_UCS_XDIR"));
            var ydir = ParseVector(RequireEnv("CPWIN_UCS_YDIR"));
            var zdir = xdir.CrossProduct(ydir);
            if (zdir.Length < 1e-12)
                throw new InvalidOperationException($"X方向とY方向が平行で UCS が定義できない: X={xdir} Y={ydir}");
            ed.CurrentUserCoordinateSystem = Matrix3d.AlignCoordinateSystem(
                Point3d.Origin, Vector3d.XAxis, Vector3d.YAxis, Vector3d.ZAxis,
                origin, xdir, ydir, zdir);
            ed.WriteMessage("\nCPWINSETUCS_OK");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nCPWINSETUCS_FAILED={ex}");
            throw;
        }
    }

    /// <summary>
    /// 計測条件（ビュー）を厳密に設定する。TARGET・VIEWTWIST を DVIEW 等のプロンプト列で作るのは
    /// 版・ロケール依存で脆いため、ViewTableRecord を直接与える。
    /// Target・ViewDirection は公式仕様どおり WCS、CenterPoint は DCS として扱う。
    /// </summary>
    [CommandMethod("CPWINSETVIEW", CommandFlags.Modal)]
    public void SetView()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            using var view = ed.GetCurrentView();
            view.Target = ParsePoint(RequireEnv("CPWIN_VIEW_TARGET"));
            view.ViewDirection = ParseVector(RequireEnv("CPWIN_VIEW_DIR"));
            view.ViewTwist = ParseDouble(RequireEnv("CPWIN_VIEW_TWIST_DEG")) * Math.PI / 180.0;
            var center = ParsePoint(RequireEnv("CPWIN_VIEW_CENTER"));
            view.CenterPoint = new Point2d(center.X, center.Y);
            view.Height = ParseDouble(RequireEnv("CPWIN_VIEW_HEIGHT"));
            view.Width = ParseDouble(RequireEnv("CPWIN_VIEW_WIDTH"));
            ed.SetCurrentView(view);
            ed.WriteMessage("\nCPWINSETVIEW_OK");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nCPWINSETVIEW_FAILED={ex}");
            throw;
        }
    }

    /// <summary>
    /// 空間取り違え回帰用に、同一図面の Model と PETA_A3 へ意図的に異なるページ設定を焼く。
    /// Model=A4・90度・Fit、PETA_A3=A3・0度・1:1 とし、どちらを使ったかを成果物寸法で識別できる。
    /// 作業コピーだけに使い、呼び出し側は保存せず閉じる。
    /// </summary>
    [CommandMethod("CPSETTESTPAGES", CommandFlags.Modal)]
    public void SetAsymmetricPageSettings()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        try
        {
            using var tr = doc.Database.TransactionManager.StartTransaction();
            ConfigureLayout(tr, LayoutManager.Current.GetLayoutId("Model"),
                "ISO_full_bleed_A4_(210.00_x_297.00_MM)", PlotRotation.Degrees090,
                Autodesk.AutoCAD.DatabaseServices.PlotType.Extents, StdScaleType.ScaleToFit, centered: true);
            ConfigureLayout(tr, LayoutManager.Current.GetLayoutId("PETA_A3"),
                "ISO_full_bleed_A3_(420.00_x_297.00_MM)", PlotRotation.Degrees000,
                Autodesk.AutoCAD.DatabaseServices.PlotType.Layout, StdScaleType.StdScale1To1, centered: false);
            tr.Commit();
            ed.WriteMessage("\nCPSETTESTPAGES_OK Model=A4/90/Fit PETA_A3=A3/0/1:1");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nCPSETTESTPAGES_FAILED={ex}");
            throw;
        }
    }

    static void ConfigureLayout(Transaction tr, ObjectId layoutId, string media,
                                PlotRotation rotation, Autodesk.AutoCAD.DatabaseServices.PlotType plotType,
                                StdScaleType scale, bool centered)
    {
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
        using var ps = new PlotSettings(layout.ModelType);
        ps.CopyFrom(layout);
        var psv = PlotSettingsValidator.Current;
        psv.SetPlotConfigurationName(ps, PdfDeviceName, null);
        psv.SetCanonicalMediaName(ps, media);
        psv.SetPlotPaperUnits(ps, PlotPaperUnit.Millimeters);
        psv.SetPlotRotation(ps, rotation);
        psv.SetPlotType(ps, plotType);
        psv.SetUseStandardScale(ps, true);
        psv.SetStdScaleType(ps, scale);
        if (plotType != Autodesk.AutoCAD.DatabaseServices.PlotType.Layout)
            psv.SetPlotCentered(ps, centered);
        layout.CopyFrom(ps);
    }

    /// <summary>
    /// ページ設定pairwiseケース（tests/tools/regression/page_setup_pairwise_cases.ps1）を
    /// 環境変数駆動で対象レイアウトへ焼く。用紙探索の水準は実在デバイスの列挙から決定的に選ぶ。
    ///   SameName              = PDFデバイス自身のA3全面用紙（同名・同寸法）
    ///   DifferentNameSameSize = PDFデバイスに無い名前でA3同寸法を持つ他デバイスの用紙
    ///   NoMatchingSize        = PDFデバイスのどの用紙とも寸法が1mm超離れる他デバイスの用紙
    /// 対比レイアウト（非対象側）はPDFデバイスのA2・回転0・Fitへ固定し、
    /// 空間取り違えを成果物寸法で識別できるようにする。作業コピーだけに使い、保存せず閉じる。
    /// </summary>
    [CommandMethod("CPSETPAIRWISE", CommandFlags.Modal)]
    public void SetPairwisePageSetup()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        try
        {
            string id = RequireEnv("CADPETA_PSP_ID");
            string targetLayout = RequireEnv("CADPETA_PSP_TARGET");
            string units = RequireEnv("CADPETA_PSP_UNITS");
            string scaleKind = RequireEnv("CADPETA_PSP_SCALEKIND");
            string mediaScenario = RequireEnv("CADPETA_PSP_MEDIA");
            int rotation = (int)ParseDouble(RequireEnv("CADPETA_PSP_ROTATION"));
            double numerator = scaleKind == "Custom" ? ParseDouble(RequireEnv("CADPETA_PSP_NUM")) : 1;
            double denominator = scaleKind == "Custom" ? ParseDouble(RequireEnv("CADPETA_PSP_DEN")) : 1;
            string contrastLayout = targetLayout == "Model" ? "PETA_A3" : "Model";

            var scenario = SelectScenarioMedia(mediaScenario);
            using var tr = doc.Database.TransactionManager.StartTransaction();
            ConfigurePairwiseLayout(tr, LayoutManager.Current.GetLayoutId(targetLayout),
                scenario.Device, scenario.Media, units, scaleKind, numerator, denominator, rotation);
            ConfigureLayout(tr, LayoutManager.Current.GetLayoutId(contrastLayout),
                "ISO_full_bleed_A2_(594.00_x_420.00_MM)", PlotRotation.Degrees000,
                contrastLayout == "Model" ? Autodesk.AutoCAD.DatabaseServices.PlotType.Extents
                                          : Autodesk.AutoCAD.DatabaseServices.PlotType.Layout,
                StdScaleType.ScaleToFit, centered: true);
            tr.Commit();
            ed.WriteMessage(string.Format(CultureInfo.InvariantCulture,
                "\nCPSETPAIRWISE_OK id={0} target={1} device={2} media={3} pageWmm={4:F2} pageHmm={5:F2} contrast=A2",
                id, targetLayout, scenario.Device, scenario.Media, scenario.SizeMm.X, scenario.SizeMm.Y));
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nCPSETPAIRWISE_FAILED={ex}");
            throw;
        }
    }

    static void ConfigurePairwiseLayout(Transaction tr, ObjectId layoutId, string device, string media,
                                        string units, string scaleKind, double numerator, double denominator,
                                        int rotationDegrees)
    {
        var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForWrite);
        using var ps = new PlotSettings(layout.ModelType);
        ps.CopyFrom(layout);
        var psv = PlotSettingsValidator.Current;
        psv.SetPlotConfigurationName(ps, device, null);
        psv.RefreshLists(ps);
        psv.SetCanonicalMediaName(ps, media);
        psv.SetPlotPaperUnits(ps, units == "Inches" ? PlotPaperUnit.Inches : PlotPaperUnit.Millimeters);
        psv.SetPlotRotation(ps, rotationDegrees switch
        {
            0 => PlotRotation.Degrees000,
            90 => PlotRotation.Degrees090,
            180 => PlotRotation.Degrees180,
            270 => PlotRotation.Degrees270,
            _ => throw new InvalidOperationException($"未対応の回転角: {rotationDegrees}"),
        });
        // Model は Extents、Paper は Layout（CPSETTESTPAGES と同じ空間別の既定形）
        psv.SetPlotType(ps, layout.ModelType
            ? Autodesk.AutoCAD.DatabaseServices.PlotType.Extents
            : Autodesk.AutoCAD.DatabaseServices.PlotType.Layout);
        if (scaleKind == "Fit")
        {
            psv.SetUseStandardScale(ps, true);
            psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);
        }
        else
        {
            psv.SetUseStandardScale(ps, false);
            psv.SetCustomPrintScale(ps, new CustomScale(numerator, denominator));
        }
        if (layout.ModelType)
            psv.SetPlotCentered(ps, true);
        layout.CopyFrom(ps);
    }

    /// <summary>
    /// 用紙探索水準に対応する実在デバイス・用紙を決定的に選ぶ（デバイス名・用紙名の序数順）。
    /// 全デバイス×全用紙へ SetCanonicalMediaName を発行すると1デバイス数十秒かかり時間切れになるため、
    /// 寸法は Autodesk 正式名の埋め込み（"..._(420.00_x_297.00_MM)"）から先に解析し、
    /// 名前に寸法を持たない候補だけ実プローブで確かめる。
    /// </summary>
    static (string Device, string Media, Point2d SizeMm) SelectScenarioMedia(string scenario)
    {
        const double a3WidthMm = 420, a3HeightMm = 297, toleranceMm = 1.0;
        const int maxProbesPerDevice = 60;
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        var pdfNames = EnumerateDeviceMediaNames(PdfDeviceName);
        if (scenario == "SameName")
        {
            const string name = "ISO_full_bleed_A3_(420.00_x_297.00_MM)";
            if (!pdfNames.Contains(name))
                throw new InvalidOperationException($"PDFデバイスに {name} がない");
            return (PdfDeviceName, name, GetMediaSize(PdfDeviceName, name));
        }
        // NoMatchingSize の判定基準になる PDF デバイスの寸法集合。名前埋め込みから解析し、
        // 解析できない名前だけ実プローブで補う
        var pdfSizes = new List<Point2d>();
        foreach (string name in pdfNames)
        {
            var parsed = TryParseSizeFromCanonicalName(name);
            pdfSizes.Add(parsed ?? GetMediaSize(PdfDeviceName, name));
        }
        var psv = PlotSettingsValidator.Current;
        var devices = new List<string>();
        foreach (string? device in psv.GetPlotDeviceList())
        {
            if (string.IsNullOrEmpty(device)) continue;
            if (string.Equals(device, PdfDeviceName, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(device, "None", StringComparison.OrdinalIgnoreCase) || device == "なし") continue;
            devices.Add(device!);
        }
        devices.Sort(StringComparer.Ordinal);
        var attempts = new StringBuilder();
        foreach (string device in devices)
        {
            // 走査証跡。時間切れ・停止時にどのデバイスまで進んだかを stdout から特定できるようにする
            ed.WriteMessage($"\nCPSETPAIRWISE_SCAN device={device}");
            List<string> names;
            try { names = EnumerateDeviceMediaNames(device).ToList(); }
            catch (System.Exception ex) { attempts.Append($"[{device}: {ex.Message}]"); continue; }
            names.Sort(StringComparer.Ordinal);
            int probes = 0;
            foreach (string name in names)
            {
                if (pdfNames.Contains(name)) continue; // 両水準とも「PDFデバイスに無い名前」だけが候補
                Point2d size;
                var parsed = TryParseSizeFromCanonicalName(name);
                if (parsed is { } value)
                {
                    size = value;
                }
                else
                {
                    if (++probes > maxProbesPerDevice) break; // 実プローブは高価なためデバイスごとに上限を置く
                    try { size = GetMediaSize(device, name); }
                    catch (System.Exception) { continue; }
                }
                double w = size.X, h = size.Y;
                if (double.IsNaN(w) || double.IsNaN(h) || w < 50 || h < 50 || w > 5000 || h > 5000) continue;
                bool hit = scenario switch
                {
                    "DifferentNameSameSize" => SizeMatches(w, h, a3WidthMm, a3HeightMm, toleranceMm),
                    "NoMatchingSize" => !pdfSizes.Any(p => SizeMatches(w, h, p.X, p.Y, toleranceMm)),
                    _ => throw new InvalidOperationException($"未知の用紙探索水準: {scenario}"),
                };
                if (hit) return (device, name, size);
            }
        }
        throw new InvalidOperationException(
            $"用紙探索水準 {scenario} を満たす実在用紙が見つからない。devices=[{string.Join(", ", devices)}] {attempts}");
    }

    static bool SizeMatches(double w, double h, double targetW, double targetH, double toleranceMm) =>
        (Math.Abs(w - targetW) <= toleranceMm && Math.Abs(h - targetH) <= toleranceMm)
        || (Math.Abs(w - targetH) <= toleranceMm && Math.Abs(h - targetW) <= toleranceMm);

    /// <summary>Autodesk 正式名の寸法埋め込み（"..._(W_x_H_MM|Inches|Pixels)"）を mm へ解析する。</summary>
    static Point2d? TryParseSizeFromCanonicalName(string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(name,
            @"_\(([0-9.]+)_x_([0-9.]+)_(MM|Inches|Pixels)\)$");
        if (!match.Success) return null;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double w)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double h))
            return null;
        return match.Groups[3].Value switch
        {
            "MM" => new Point2d(w, h),
            "Inches" => new Point2d(w * 25.4, h * 25.4),
            _ => new Point2d(double.NaN, double.NaN), // Pixels は mm と比較できないため範囲チェックで弾く
        };
    }

    /// <summary>デバイスの用紙正式名を列挙する（名前だけなら1呼び出しで安価に得られる）。</summary>
    static HashSet<string> EnumerateDeviceMediaNames(string device)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId("Model"), OpenMode.ForRead);
        using var probe = new PlotSettings(layout.ModelType);
        probe.CopyFrom(layout);
        var psv = PlotSettingsValidator.Current;
        psv.SetPlotConfigurationName(probe, device, null);
        psv.RefreshLists(probe);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? name in psv.GetCanonicalMediaNameList(probe))
            if (!string.IsNullOrEmpty(name)) names.Add(name!);
        tr.Commit();
        return names;
    }

    /// <summary>用紙1件の公称寸法（PlotPaperSize、mm）を実プローブで取得する。</summary>
    static Point2d GetMediaSize(string device, string media)
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        using var tr = doc.Database.TransactionManager.StartTransaction();
        var layout = (Layout)tr.GetObject(LayoutManager.Current.GetLayoutId("Model"), OpenMode.ForRead);
        using var probe = new PlotSettings(layout.ModelType);
        probe.CopyFrom(layout);
        var psv = PlotSettingsValidator.Current;
        psv.SetPlotConfigurationName(probe, device, null);
        psv.RefreshLists(probe);
        psv.SetCanonicalMediaName(probe, media);
        var size = probe.PlotPaperSize;
        tr.Commit();
        return size;
    }

    [CommandMethod("CPWINPROBE", CommandFlags.Modal)]
    public void Run()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        var record = new Dictionary<string, object?>();

        string outDir = RequireEnv("CPWIN_OUT");
        string caseName = RequireEnv("CPWIN_CASE");
        Directory.CreateDirectory(outDir);
        string jsonPath = Path.Combine(outDir, $"{caseName}.probe.json");

        try
        {
            RunCore(doc, ed, record, outDir, caseName);
            record["fatal"] = null;
        }
        catch (System.Exception ex)
        {
            // 握りつぶさない。連鎖ごと記録し、記録の書き出しは必ず行う
            record["fatal"] = ex.ToString();
            ed.WriteMessage($"\nCPWINPROBE 失敗: {ex.Message}");
        }
        finally
        {
            File.WriteAllText(jsonPath,
                JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            ed.WriteMessage($"\nCPWINPROBE_JSON={jsonPath}");
        }
    }

    void RunCore(Document doc, Editor ed, Dictionary<string, object?> record, string outDir, string caseName)
    {
        record["case"] = caseName;
        // .scr が与えた入力文字列。API の戻り値と区別して記録する（両者が一致する保証はない）
        record["inputString1"] = Environment.GetEnvironmentVariable("CPWIN_IN1");
        record["inputString2"] = Environment.GetEnvironmentVariable("CPWIN_IN2");

        // pick は製品コードと同じ API・同じ順序で行う（GetPoint → GetCorner）
        var first = ed.GetPoint(new PromptPointOptions("\n最初のコーナーを指定"));
        if (first.Status != PromptStatus.OK)
            throw new InvalidOperationException($"最初のコーナーの取得に失敗: Status={first.Status}");
        var second = ed.GetCorner(new PromptCornerOptions("\nもう一方のコーナーを指定", first.Value));
        if (second.Status != PromptStatus.OK)
            throw new InvalidOperationException($"もう一方のコーナーの取得に失敗: Status={second.Status}");

        record["apiPoint1"] = Xyz(first.Value);
        record["apiPoint2"] = Xyz(second.Value);

        var ucs = ed.CurrentUserCoordinateSystem;
        record["ucsMatrix"] = MatrixValues(ucs);
        record["ucsOriginInWcs"] = Xyz(Point3d.Origin.TransformBy(ucs));

        using (var view = ed.GetCurrentView())
        {
            // 公式仕様では Target・ViewDirection は WCS、CenterPoint は DCS。実測で突き合わせる
            record["view"] = new Dictionary<string, object?>
            {
                ["target"] = Xyz(view.Target),
                ["viewDirection"] = Xyz(view.ViewDirection),
                ["viewTwistRad"] = view.ViewTwist,
                ["centerPoint"] = new[] { view.CenterPoint.X, view.CenterPoint.Y },
                ["height"] = view.Height,
                ["width"] = view.Width,
            };
        }

        record["sysvars"] = ReadSysvars(
            "TILEMODE", "CTAB", "MEASUREMENT", "WORLDUCS",
            "UCSORG", "UCSXDIR", "UCSYDIR",
            "TARGET", "VIEWDIR", "VIEWTWIST", "VIEWCTR", "VIEWSIZE");

        // 3候補を同一条件で発行する。候補ごとの失敗は記録して次へ進む（1つの失敗が他を隠さない）
        var candidates = new Dictionary<string, object?>();
        foreach (string kind in CandidateKinds)
        {
            var entry = new Dictionary<string, object?>();
            try
            {
                var (p1, p2) = MapCandidate(kind, first.Value, second.Value, ed);
                entry["mappedPoint1"] = Xyz(p1);
                entry["mappedPoint2"] = Xyz(p2);
                var win = new Extents2d(
                    Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
                    Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y));
                entry["window"] = Rect(win);
                string pdf = Path.Combine(outDir, $"{caseName}_{kind}.pdf");
                PlotWindowTo(doc, win, pdf, entry);
                entry["pdf"] = pdf;
                entry["pdfBytes"] = new FileInfo(pdf).Length;
                entry["error"] = null;
            }
            catch (System.Exception ex)
            {
                entry["error"] = ex.ToString();
            }
            candidates[kind] = entry;
        }
        record["candidates"] = candidates;
    }

    /// <summary>
    /// pick 点（現在UCS）を候補ごとの座標系へ写す。
    /// wcs2dcs は Autodesk 公式の DCS→WCS 行列（ねじれ→目標点変位→視線方向の平面→世界）の逆行列を
    /// WCS 点へ掛ける。Target・ViewDirection は公式仕様どおり WCS 値として扱う。
    /// </summary>
    static (Point3d, Point3d) MapCandidate(string kind, Point3d p1, Point3d p2, Editor ed)
    {
        switch (kind)
        {
            case "raw":
                return (p1, p2);
            case "ucs2wcs":
                {
                    var m = ed.CurrentUserCoordinateSystem;
                    return (p1.TransformBy(m), p2.TransformBy(m));
                }
            case "wcs2dcs":
                {
                    var m = ed.CurrentUserCoordinateSystem;
                    using var view = ed.GetCurrentView();
                    var dcs2wcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target)
                                * Matrix3d.Displacement(view.Target - Point3d.Origin)
                                * Matrix3d.PlaneToWorld(view.ViewDirection);
                    var wcs2dcs = dcs2wcs.Inverse();
                    return (p1.TransformBy(m).TransformBy(wcs2dcs), p2.TransformBy(m).TransformBy(wcs2dcs));
                }
            default:
                throw new NotSupportedException($"未知の候補: {kind}");
        }
    }

    /// <summary>
    /// 窓プロットを1件発行する。座標以外の条件（デバイス・用紙・回転・尺度・中央合わせ・スタイル）は
    /// 全候補で同一にし、差が座標だけになるようにする。設定直後と検証後の窓を読み戻して記録する。
    /// </summary>
    void PlotWindowTo(Document doc, Extents2d window, string outputPdfPath, Dictionary<string, object?> entry)
    {
        var db = doc.Database;
        object savedBackgroundPlot = Application.GetSystemVariable("BACKGROUNDPLOT");
        Application.SetSystemVariable("BACKGROUNDPLOT", 0);
        using var tr = db.TransactionManager.StartTransaction();
        try
        {
            var layoutId = LayoutManager.Current.GetLayoutId(LayoutManager.Current.CurrentLayout);
            var layout = (Layout)tr.GetObject(layoutId, OpenMode.ForRead);
            var psv = PlotSettingsValidator.Current;

            using var ps = new PlotSettings(layout.ModelType);
            ps.CopyFrom(layout);
            string layoutMedia = ps.CanonicalMediaName;
            psv.SetPlotConfigurationName(ps, PdfDeviceName, null);
            entry["mediaAfterDeviceReset"] = ps.CanonicalMediaName;
            entry["mediaRestored"] = false;
            if (!string.IsNullOrEmpty(layoutMedia))
            {
                try
                {
                    psv.SetCanonicalMediaName(ps, layoutMedia);
                    entry["mediaRestored"] = true;
                }
                catch (CadRuntimeException ex)
                {
                    // 用紙が新デバイスに無い場合。事実を残す（既定用紙のまま進む）
                    entry["mediaRestoreFailed"] = $"{layoutMedia}: {ex.ErrorStatus}";
                }
            }
            entry["mediaUsed"] = ps.CanonicalMediaName;

            // 窓を先に設定してから種別を Window にする（順序を変えると設定が不定になる）
            psv.SetPlotWindowArea(ps, window);
            psv.SetPlotType(ps, Autodesk.AutoCAD.DatabaseServices.PlotType.Window);
            psv.SetPlotCentered(ps, true);
            entry["windowAfterSet"] = Rect(ps.PlotWindowArea);
            entry["plotTypeAfterSet"] = ps.PlotType.ToString();

            psv.SetUseStandardScale(ps, true);
            psv.SetStdScaleType(ps, StdScaleType.ScaleToFit);

            var plotInfo = new PlotInfo { Layout = layoutId, OverrideSettings = ps };
            var piv = new PlotInfoValidator { MediaMatchingPolicy = MatchingPolicy.MatchEnabled };
            piv.Validate(plotInfo);
            // 検証器が窓を書き換えるかを見る（書き換えるなら候補比較の前提が変わる）
            entry["windowAfterValidate"] = Rect(ps.PlotWindowArea);
            entry["plotTypeAfterValidate"] = ps.PlotType.ToString();
            entry["paperUnits"] = ps.PlotPaperUnits.ToString();
            entry["plotRotation"] = ps.PlotRotation.ToString();

            // DCS はビューに依存する座標系のため、窓を確定してから発行までの間にビューが変わると
            // 領域がずれ得る。PETA は窓 pick のスコープを抜けた後に発行するので、この経路は実在する。
            // 「発行直前にビューを動かす」条件を作って、ずれるかどうかを実測する。
            string? shift = Environment.GetEnvironmentVariable("CPWIN_SHIFT_VIEW_BEFORE_PUBLISH");
            if (!string.IsNullOrEmpty(shift))
            {
                var d = ParseTriple(shift!);
                var ed2 = doc.Editor;
                using (var v = ed2.GetCurrentView())
                {
                    v.Target = new Point3d(v.Target.X + d[0], v.Target.Y + d[1], v.Target.Z + d[2]);
                    v.CenterPoint = new Point2d(v.CenterPoint.X + d[0], v.CenterPoint.Y + d[1]);
                    ed2.SetCurrentView(v);
                }
                using (var v2 = ed2.GetCurrentView())
                    entry["viewTargetBeforePublish"] = Xyz(v2.Target);
            }

            if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
                throw new InvalidOperationException($"プロット処理中: {PlotFactory.ProcessPlotState}");
            string stage = "BeginPlot";
            try
            {
                using var engine = PlotFactory.CreatePublishEngine();
                engine.BeginPlot(null, null);
                stage = "BeginDocument";
                engine.BeginDocument(plotInfo, doc.Name, null, 1, true, outputPdfPath);
                stage = "BeginPage";
                using (var pageInfo = new PlotPageInfo())
                    engine.BeginPage(pageInfo, plotInfo, true, null);
                stage = "GenerateGraphics";
                engine.BeginGenerateGraphics(null);
                engine.EndGenerateGraphics(null);
                stage = "EndPage";
                engine.EndPage(null);
                stage = "EndDocument";
                engine.EndDocument(null);
                stage = "EndPlot";
                engine.EndPlot(null);
            }
            catch (CadRuntimeException ex)
            {
                throw new InvalidOperationException(
                    $"発行に失敗 stage={stage} status={ex.ErrorStatus} out={outputPdfPath}", ex);
            }
            if (!File.Exists(outputPdfPath))
                throw new IOException($"PDF が生成されていない: {outputPdfPath}");
            // 図面を書き換えないため、読み取りのみのトランザクションは破棄する
            tr.Abort();
        }
        finally
        {
            Application.SetSystemVariable("BACKGROUNDPLOT", savedBackgroundPlot);
        }
    }

    static Dictionary<string, object?> ReadSysvars(params string[] names)
    {
        var result = new Dictionary<string, object?>();
        foreach (string n in names)
        {
            try
            {
                object v = Application.GetSystemVariable(n);
                result[n] = v is Point3d p ? Xyz(p) : v is Point2d p2 ? new[] { p2.X, p2.Y } : v?.ToString();
            }
            catch (System.Exception ex)
            {
                // 版で存在しない変数があり得る。事実として残す
                result[n] = $"<取得失敗: {ex.GetType().Name}>";
            }
        }
        return result;
    }

    static double[] Xyz(Point3d p) => new[] { p.X, p.Y, p.Z };
    static double[] Xyz(Vector3d v) => new[] { v.X, v.Y, v.Z };
    static double[] Rect(Extents2d e) => new[] { e.MinPoint.X, e.MinPoint.Y, e.MaxPoint.X, e.MaxPoint.Y };
    static double[] MatrixValues(Matrix3d m) => m.ToArray();

    static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"環境変数 {name} が未設定（計測条件が確定しない）");

    /// <summary>"x,y,z" を解析する。桁を落とさないよう不変カルチャで厳密に読む。</summary>
    static double[] ParseTriple(string s)
    {
        string[] parts = s.Split(',');
        if (parts.Length != 3)
            throw new FormatException($"3成分の \"x,y,z\" ではない: \"{s}\"");
        var v = new double[3];
        for (int i = 0; i < 3; i++)
            v[i] = ParseDouble(parts[i]);
        return v;
    }

    static Point3d ParsePoint(string s) { var v = ParseTriple(s); return new Point3d(v[0], v[1], v[2]); }
    static Vector3d ParseVector(string s) { var v = ParseTriple(s); return new Vector3d(v[0], v[1], v[2]); }

    static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : throw new FormatException($"数値として読めない: \"{s}\"");
}
