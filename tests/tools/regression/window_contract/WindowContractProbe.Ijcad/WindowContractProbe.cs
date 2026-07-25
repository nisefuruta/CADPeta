// 窓プロットの座標契約を実機で確定する計測器（IJCAD 2026 SDK 移植版）。
//
// 背景: PlotSettingsValidator.SetPlotWindowArea が期待する座標系について、Autodesk 公式仕様は DCS と
// 明記する一方、UCS が回転した実図面では現行の UCS→WCS 経路が空の結果を出す。どちらが正しいかは
// 実測で確定するほかない。本計測器は pick した2点から3候補（生値・UCS→WCS・WCS→DCS）を作り、
// 座標以外の条件を完全に揃えて別々の PDF を発行する。合わせて、判断の材料になる値を全て記録する。
// AutoCAD 実機では DCS が正しいと確定済みだが、IJCAD は同じ SDK 形状でも契約が同じとは限らないため
// 未実測であり、本計測器を IJCAD で走らせて確定させる（src/CadPeta.Plugin.Ijcad/IjcadPluginDefaults.cs
// の WindowPickContract は本計測の結果を反映する）。
//
// 設計方針:
// - 製品コードを参照しない。測定対象の実装を使って契約を測ると循環する。
// - 例外を握りつぶさない。候補ごとに失敗を記録し、1つの失敗が他を隠さないようにする。
// - コンソールの日本語出力は化けるため、記録は JSON でファイルへ書く。
//
// 移植メモ（AutoCAD 版との意図的な差分）:
// - using を IJCAD SDK（Itjp.Ijcad.*）へ変更した。API 形状は AutoCAD とほぼ同一
//   （2026-07-16 実 DLL のリフレクションで Matrix3d.AlignCoordinateSystem 等の存在を確認済み）。
// - JSON は System.Text.Json を使わず手書きの最小シリアライザ（MiniJson）で書く。
//   NETLOAD 先ホスト（IJCAD）のアセンブリ束縛は AutoCAD と別系統で、System.Text.Json の版が
//   ホスト提供のものと食い違うと FileLoadException になり得る。この依存束縛問題を持ち込まないため。
// - 計測条件は環境変数だけでなくパラメータファイル（CPWIN_PARAMS）にも対応する。GUI セッションでは
//   1プロセスで複数ケースを連続して流すが、プロセス環境変数は起動時に固定され後から書き換えられない
//   ため、ケースごとに変わる値をファイル経由で渡す（GetParam 参照）。
using System.Globalization;
using System.Text;
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.EditorInput;
using Itjp.Ijcad.Geometry;
using Itjp.Ijcad.PlottingServices;
using Itjp.Ijcad.Runtime;
// Application は Itjp.Ijcad.ApplicationServices と .Core の両方に存在し、両 using では
// あいまい参照になる（src/CadPeta.Plugin.CadCommon/CadPlotEngineRunner.cs と同じ実測）。
// DocumentManager・GetSystemVariable 等を持つ .Core 側を CadApplication として一意に使う
using CadApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
using CadRuntimeException = Itjp.Ijcad.Runtime.Exception;

[assembly: CommandClass(typeof(CadPeta.Probe.Ijcad.WindowContractProbe))]

namespace CadPeta.Probe.Ijcad;

public sealed class WindowContractProbe
{
    /// <summary>発行先デバイス。製品コードの既定と同一にして条件を揃える
    /// （src/CadPeta.Plugin.Ijcad/IjcadPluginDefaults.cs と同じ値）。</summary>
    const string PdfDeviceName = "DWG To PDF.pc3";

    /// <summary>候補の識別子。成果物名・記録のキーに使う唯一の情報源。</summary>
    static readonly string[] CandidateKinds = { "raw", "ucs2wcs", "wcs2dcs" };

    /// <summary>
    /// 計測条件（UCS）を厳密に設定する。UCS コマンドのプロンプト列は版・ロケール依存で脆いため、
    /// 行列を直接与えて決定的にする。原点・X方向・Y方向を環境変数（またはパラメータファイル）で受ける。
    /// </summary>
    [CommandMethod("CPWINSETUCS", CommandFlags.Modal)]
    public void SetUcs()
    {
        var ed = CadApplication.DocumentManager.MdiActiveDocument.Editor;
        try
        {
            var origin = ParsePoint(RequireParam("CPWIN_UCS_ORIGIN"));
            var xdir = ParseVector(RequireParam("CPWIN_UCS_XDIR"));
            var ydir = ParseVector(RequireParam("CPWIN_UCS_YDIR"));
            var zdir = xdir.CrossProduct(ydir);
            if (zdir.Length < 1e-12)
                throw new InvalidOperationException($"X方向とY方向が平行で UCS が定義できない: X={xdir} Y={ydir}");
            // Matrix3d.AlignCoordinateSystem は IJCAD にも同シグネチャで存在する
            // （GcDbMgd.dll のリフレクションで確認済み。無ければ原点＋3軸から同等の行列を
            // 組み立てる代替が必要だったが、IJCAD では不要だった）。
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
        var ed = CadApplication.DocumentManager.MdiActiveDocument.Editor;
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
        var doc = CadApplication.DocumentManager.MdiActiveDocument;
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
        record["inputString1"] = GetParam("CPWIN_IN1");
        record["inputString2"] = GetParam("CPWIN_IN2");

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
        object savedBackgroundPlot = CadApplication.GetSystemVariable("BACKGROUNDPLOT");
        CadApplication.SetSystemVariable("BACKGROUNDPLOT", 0);
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
            string? requestedMedia = GetParam("CPWIN_MEDIA");
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
            psv.SetPlotType(ps, Itjp.Ijcad.DatabaseServices.PlotType.Window);
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
            string? shift = GetParam("CPWIN_SHIFT_VIEW_BEFORE_PUBLISH");
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
            CadApplication.SetSystemVariable("BACKGROUNDPLOT", savedBackgroundPlot);
        }
    }

    static Dictionary<string, object?> ReadSysvars(params string[] names)
    {
        var result = new Dictionary<string, object?>();
        foreach (string n in names)
        {
            try
            {
                object v = CadApplication.GetSystemVariable(n);
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
    /// 計測条件の値を読む。環境変数だけでなくパラメータファイルにも対応する。
    /// 環境変数 CPWIN_PARAMS が指すファイル（UTF-8、1行1組の "KEY=VALUE"、キー名は移植元の
    /// 環境変数名と同じ）をコマンド実行のたびに読み直し、そこに無いキーだけ環境変数へ
    /// フォールバックする。
    /// 理由: GUI セッションでは1プロセス（NETLOAD 済みの IJCAD）で複数ケースを連続して流すが、
    /// プロセス環境変数は起動時に固定され後続ケースの値へ書き換えられない。ケースごとに変わる
    /// 値（UCS・ビュー・pick 座標等）をファイル経由で渡すことで、1プロセス内の複数ケース実行に
    /// 対応する。
    /// </summary>
    static string? GetParam(string name)
    {
        string? paramsPath = Environment.GetEnvironmentVariable("CPWIN_PARAMS");
        if (!string.IsNullOrEmpty(paramsPath) && File.Exists(paramsPath))
        {
            foreach (string line in File.ReadAllLines(paramsPath, Encoding.UTF8))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue; // 空行・コメント・"="無しは読み飛ばす
                if (string.Equals(line.Substring(0, eq).Trim(), name, StringComparison.Ordinal))
                    return line.Substring(eq + 1).Trim();
            }
        }
        return Environment.GetEnvironmentVariable(name);
    }

    static string RequireParam(string name) =>
        GetParam(name)
        ?? throw new InvalidOperationException(
            $"環境変数または CPWIN_PARAMS ファイルに {name} が未設定（計測条件が確定しない）");

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
/// 記録 JSON の手書き最小シリアライザ。System.Text.Json 等の外部依存を持たない
/// （NETLOAD 先ホストのアセンブリ束縛問題を持ち込まないため。CadPeta.* は勿論、
/// パッケージ参照も一切追加しない設計方針と対になる）。
/// 対応する型は本計測器の記録スキーマが使う範囲に絞る:
/// Dictionary&lt;string,object?&gt;（入れ子可）、object?[]、double[]、string、double、bool、null。
/// </summary>
static class MiniJson
{
    public static string Serialize(object? value)
    {
        var sb = new StringBuilder();
        Write(value, sb, 0);
        return sb.ToString();
    }

    static void Write(object? value, StringBuilder sb, int indent)
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
                // InvariantCulture の "R" 書式（往復可能な最短表現）で書く
                sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                break;
            case string s:
                WriteString(s, sb);
                break;
            case double[] arr:
                WriteArray(arr.Length, i => arr[i], sb, indent);
                break;
            case object?[] arr:
                WriteArray(arr.Length, i => arr[i], sb, indent);
                break;
            case Dictionary<string, object?> dict:
                WriteObject(dict, sb, indent);
                break;
            default:
                throw new NotSupportedException($"MiniJson が対応しない型: {value.GetType()}");
        }
    }

    static void WriteArray(int count, Func<int, object?> get, StringBuilder sb, int indent)
    {
        if (count == 0)
        {
            sb.Append("[]");
            return;
        }
        sb.Append('[').Append('\n');
        string inner = Indent(indent + 1);
        for (int i = 0; i < count; i++)
        {
            sb.Append(inner);
            Write(get(i), sb, indent + 1);
            if (i < count - 1)
                sb.Append(',');
            sb.Append('\n');
        }
        sb.Append(Indent(indent)).Append(']');
    }

    static void WriteObject(Dictionary<string, object?> dict, StringBuilder sb, int indent)
    {
        if (dict.Count == 0)
        {
            sb.Append("{}");
            return;
        }
        sb.Append('{').Append('\n');
        string inner = Indent(indent + 1);
        int i = 0;
        foreach (var kv in dict)
        {
            sb.Append(inner);
            WriteString(kv.Key, sb);
            sb.Append(": ");
            Write(kv.Value, sb, indent + 1);
            if (i < dict.Count - 1)
                sb.Append(',');
            sb.Append('\n');
            i++;
        }
        sb.Append(Indent(indent)).Append('}');
    }

    static string Indent(int level) => new string(' ', level * 2);

    /// <summary>JSON 文字列リテラルへエスケープする。二重引用符・バックスラッシュ・制御文字
    /// （0x00-0x1F）を対象にする。</summary>
    static void WriteString(string s, StringBuilder sb)
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
}
