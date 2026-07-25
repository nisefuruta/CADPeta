// 窓プロットの座標契約を実機で確定する計測器（ARES 版）。
//
// 移植元: tests/tools/regression/window_contract/WindowContractProbe（AutoCAD SDK 版）。
// 背景: PlotSettingsValidator.SetPlotWindowArea が期待する座標系について、Autodesk 公式仕様は DCS と
// 明記する一方、UCS が回転した実図面では現行の UCS→WCS 経路が空の結果を出す。AutoCAD 実機では
// DCS と確定したが、ARES では未実測（src/CadPeta.Plugin.Ares/AresPluginDefaults.cs 参照）。
// どちらが正しいかは実測で確定するほかない。本計測器は pick した2点から3候補
// （生値・UCS→WCS・WCS→DCS）を作り、座標以外の条件を完全に揃えて別々の PDF を発行する。
// 合わせて、判断の材料になる値を全て記録する。記録スキーマは移植元と完全一致させる。
//
// 設計方針:
// - 製品コード（CadPeta.Plugin.* / CadPeta.*）を参照しない。測定対象の実装を使って契約を測ると
//   循環する（移植元も同じ方針で AutoCAD SDK 専属）。
// - 例外を握りつぶさない。候補ごとに失敗を記録し、1つの失敗が他を隠さないようにする。
// - コンソールの日本語出力は化けるため、記録は JSON でファイルへ書く。
// - JSON 化は System.Text.Json を使わず手書きの最小シリアライザで行う。NETLOAD される net48 ホストへ
//   NuGet 依存を持ち込むと、同一フォルダ配備と版束縛の問題（PetaCommand.cs のアセンブリ解決フックが
//   実例として示す競合）を計測器にまで持ち込むため。
using System.Globalization;
using System.Text;
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.EditorInput;
using Teigha.Geometry;
using Teigha.PlottingServices;
using Teigha.Runtime;
using CadRuntimeException = Teigha.Runtime.Exception;

[assembly: CommandClass(typeof(CadPeta.Probe.Ares.WindowContractProbe))]

namespace CadPeta.Probe.Ares;

public sealed class WindowContractProbe
{
    /// <summary>発行先デバイス。製品コードの既定と同一にして条件を揃える
    /// （src/CadPeta.Plugin.Ares/AresPluginDefaults.cs の PdfDeviceName と同じ値）。</summary>
    const string PdfDeviceName = "PDF";

    /// <summary>候補の識別子。成果物名・記録のキーに使う唯一の情報源。</summary>
    static readonly string[] CandidateKinds = { "raw", "ucs2wcs", "wcs2dcs" };

    /// <summary>
    /// 計測条件（UCS）を厳密に設定する。UCS コマンドのプロンプト列は版・ロケール依存で脆いため、
    /// 行列を直接与えて決定的にする。原点・X方向・Y方向をパラメータで受ける。
    /// </summary>
    [CommandMethod("CPWINSETUCS", CommandFlags.Modal)]
    public void SetUcs()
    {
        var ed = Application.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            var origin = ParsePoint(RequireParam("CPWIN_UCS_ORIGIN"));
            var xdir = ParseVector(RequireParam("CPWIN_UCS_XDIR"));
            var ydir = ParseVector(RequireParam("CPWIN_UCS_YDIR"));
            var zdir = xdir.CrossProduct(ydir);
            if (zdir.Length < 1e-12)
                throw new InvalidOperationException($"X方向とY方向が平行で UCS が定義できない: X={xdir} Y={ydir}");
            // Teigha.Geometry.Matrix3d.AlignCoordinateSystem は AutoCAD の Matrix3d と同一シグネチャで
            // 存在する（ODA 由来の Teigha SDK は ObjectARX の型・API 形状をほぼそのまま提供する。
            // 2026-07-16 実 DLL のシンボル確認）。代替実装は不要だった
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
            view.Target = ParsePoint(RequireParam("CPWIN_VIEW_TARGET"));
            view.ViewDirection = ParseVector(RequireParam("CPWIN_VIEW_DIR"));
            view.ViewTwist = ParseDouble(RequireParam("CPWIN_VIEW_TWIST_DEG")) * Math.PI / 180.0;
            var center = ParsePoint(RequireParam("CPWIN_VIEW_CENTER"));
            view.CenterPoint = new Point2d(center.X, center.Y);
            view.Height = ParseDouble(RequireParam("CPWIN_VIEW_HEIGHT"));
            view.Width = ParseDouble(RequireParam("CPWIN_VIEW_WIDTH"));
            ed.SetCurrentView(view);
            ed.WriteMessage("\nCPWINSETVIEW_OK");
        }
        catch (System.Exception ex)
        {
            ed.WriteMessage($"\nCPWINSETVIEW_FAILED={ex}");
            throw;
        }
    }

    [CommandMethod("CPWINPROBE", CommandFlags.Modal)]
    public void Run()
    {
        var doc = Application.DocumentManager.MdiActiveDocument;
        var ed = doc.Editor;
        var record = new Dictionary<string, object?>();

        string outDir = RequireParam("CPWIN_OUT");
        string caseName = RequireParam("CPWIN_CASE");
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
            File.WriteAllText(jsonPath, MiniJson.Serialize(record), new UTF8Encoding(false));
            ed.WriteMessage($"\nCPWINPROBE_JSON={jsonPath}");
        }
    }

    void RunCore(Document doc, Editor ed, Dictionary<string, object?> record, string outDir, string caseName)
    {
        record["case"] = caseName;
        // .scr が与えた入力文字列。API の戻り値と区別して記録する（両者が一致する保証はない）
        record["inputString1"] = ReadParam("CPWIN_IN1");
        record["inputString2"] = ReadParam("CPWIN_IN2");

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
                // FileInfo.Length は long。MiniJson は double のみ扱うため、精度を落とさない
                // 範囲（PDF は 2^53 バイトに遠く及ばない）で double へ変換して記録する。
                // long のまま渡すと MiniJson が例外を投げ、JSON が書かれない（ARES 2024 実測）。
                entry["pdfBytes"] = (double)new FileInfo(pdf).Length;
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
    /// WCS 点へ掛ける。目標点は GetCurrentView().Target でなくシステム変数 TARGET から読む。
    /// ARES 2027 の GetCurrentView().Target はビュー中心を畳み込んだ値を返し真の目標点にならない
    /// （2026-07-17 実測: 設定 (60,50)・中心 (40,25)・ねじれ 36.87°で (107,46) が返る。2024 は設定値
    /// のまま）。一方 ARES のシステム変数 TARGET は両版とも WCS の真値を返す（AutoCAD 系の
    /// UCS 表現と違う。回転 UCS のケース G で UCS 表現 (6.6,113.8) でなく WCS (60,50) を実測）。
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
                    var target = (Point3d)Application.GetSystemVariable("TARGET");
                    var dcs2wcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, target)
                                * Matrix3d.Displacement(target - Point3d.Origin)
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
    /// 段階名・順序は src/CadPeta.Plugin.CadCommon/CadPlotEngineRunner.cs と同形にする
    /// （製品4種で共有する PlotEngine 状態機械と同じ手順を踏む）。
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
            // 用紙の決め方: CPWIN_MEDIA が与えられたらその正式名を設定する。オラクル（-PLOT）が
            // 実際に使った用紙とそろえるための口で、ハーネスがオラクルのページ設定読み戻し（DXF 4）
            // から正式名を渡す。表示名を推測して -PLOT のプロンプトへ渡す方式は、正式名と表示名の
            // 対応が製品ごとに違い成立しない（IJCAD 実測: "A4"・"User66011" とも「見つかりません」）。
            string? requestedMedia = ReadParam("CPWIN_MEDIA");
            if (!string.IsNullOrEmpty(requestedMedia))
            {
                entry["mediaRequested"] = requestedMedia;
                try
                {
                    psv.SetCanonicalMediaName(ps, requestedMedia);
                    entry["mediaRestored"] = true;
                }
                catch (CadRuntimeException ex)
                {
                    entry["mediaRestoreFailed"] = $"{requestedMedia}: {ex.ErrorStatus}";
                }
            }
            else if (!string.IsNullOrEmpty(layoutMedia))
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
            psv.SetPlotType(ps, Teigha.DatabaseServices.PlotType.Window);
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
            string? shift = ReadParam("CPWIN_SHIFT_VIEW_BEFORE_PUBLISH");
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

    /// <summary>
    /// パラメータファイル優先で計測条件を必須取得する。無ければ従来どおり環境変数へ落ちる
    /// （フォールバック規則は ReadParam と同じ）。
    /// </summary>
    static string RequireParam(string name) =>
        ReadParam(name)
        ?? throw new InvalidOperationException($"パラメータ {name} が未設定（計測条件が確定しない。CPWIN_PARAMS または環境変数のどちらにも無い）");

    /// <summary>
    /// 計測条件を1件読む。環境変数 CPWIN_PARAMS が指すパラメータファイル（UTF-8、1行1組の
    /// KEY=VALUE）を優先し、無ければ従来どおり同名の環境変数へフォールバックする。
    /// GUI セッションは1プロセスで複数ケースを連続して流すが、プロセス環境変数は起動時に固定され
    /// ケースごとに書き換えられない。パラメータファイルはケースごとに用意し直せるため、
    /// この呼び出しの**たびに**ファイルを読み直す（プロセス生存中は固定という前提を置かない）。
    /// </summary>
    static string? ReadParam(string name)
    {
        string? paramsPath = Environment.GetEnvironmentVariable("CPWIN_PARAMS");
        if (!string.IsNullOrEmpty(paramsPath) && File.Exists(paramsPath))
        {
            foreach (string rawLine in File.ReadAllLines(paramsPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;
                int eq = line.IndexOf('=');
                if (eq < 0)
                    continue;
                if (string.Equals(line.Substring(0, eq).Trim(), name, StringComparison.Ordinal))
                    return line.Substring(eq + 1).Trim();
            }
        }
        return Environment.GetEnvironmentVariable(name);
    }

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

/// <summary>
/// JSON の最小手書きシリアライザ。System.Text.Json を使わない理由はファイル先頭のコメントを参照。
/// 対応型: Dictionary&lt;string,object?&gt;（入れ子可）・object?[]・double[]・string・double・bool・null。
/// インデント2文字で、System.Text.Json の WriteIndented=true に近い見た目にする（人が読める記録が目的）。
/// </summary>
static class MiniJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(sb, value, 0);
        return sb.ToString();
    }

    static void Write(StringBuilder sb, object? value, int indent)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                WriteDouble(sb, d);
                break;
            case string s:
                WriteString(sb, s);
                break;
            case double[] arr:
                WriteArray(sb, arr.Length, i => Write(sb, arr[i], indent + 1), indent);
                break;
            case object?[] arr:
                WriteArray(sb, arr.Length, i => Write(sb, arr[i], indent + 1), indent);
                break;
            case Dictionary<string, object?> map:
                WriteObject(sb, map, indent);
                break;
            default:
                // 記録スキーマは呼び出し側が固定する。未対応型が紛れ込んだら黙って ToString() へ
                // 落とさず、原因箇所を特定できるよう例外にする
                throw new NotSupportedException($"MiniJson が扱えない型: {value.GetType()}");
        }
    }

    static void WriteDouble(StringBuilder sb, double d)
    {
        // 記録対象は座標・角度・長さ等の有限実数のみを前提とする。NaN/Infinity は JSON で表現できず、
        // 無音で壊れた JSON を書くと後段（compare_pages.py 等）の解析が原因不明で落ちるため、
        // ここで打ち切って原因箇所を明示する
        if (double.IsNaN(d) || double.IsInfinity(d))
            throw new NotSupportedException($"JSON化できない実数値: {d}");
        sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20)
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    static void WriteArray(StringBuilder sb, int count, Action<int> writeElement, int indent)
    {
        if (count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[').Append('\n');
        for (int i = 0; i < count; i++)
        {
            Indent(sb, indent + 1);
            writeElement(i);
            sb.Append(i < count - 1 ? ",\n" : "\n");
        }
        Indent(sb, indent);
        sb.Append(']');
    }

    static void WriteObject(StringBuilder sb, Dictionary<string, object?> map, int indent)
    {
        if (map.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append('{').Append('\n');
        int i = 0;
        int count = map.Count;
        foreach (var kv in map)
        {
            Indent(sb, indent + 1);
            WriteString(sb, kv.Key);
            sb.Append(": ");
            Write(sb, kv.Value, indent + 1);
            sb.Append(++i < count ? ",\n" : "\n");
        }
        Indent(sb, indent);
        sb.Append('}');
    }

    static void Indent(StringBuilder sb, int level) => sb.Append(' ', level * 2);
}
