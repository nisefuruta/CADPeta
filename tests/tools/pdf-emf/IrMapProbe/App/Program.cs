// IrMapProbe: プロットPDF → SVG IR 写像の検証ツール（Phase 0 仮説1の最終工程）
//
// PdfPigProbe（第3段）の状態機械を流用・拡張し、検証済みの全属性を SVG IR
// 制約プロファイル（設計書§2）へ写像して SVG と診断ログ（設計書§7）を書き出す。
// - パス: ベジェ制御点保持・色・線幅（CTM補正）・fill-rule
// - テキスト: Tz→幅係数、グリフ位置、フォント名写像
// - 透過: ExtGState ca/CA → fill-opacity / stroke-opacity
// - レイヤ: /OC BDC → data-layer
// - クリップ: W/W* + n → clipPath 参照
// - 画像: JPEG + SMask を合成した PNG を data:URI 埋め込み（SOLID×透過の非可逆類型）
// - 座標: pt → mm 正規化、Y軸反転（設計書§2.2）
//
// 前提（検証記録_PDFBox照合.md §4 で実証済み）:
// - 描画オペレータと Page.Paths は 1:1（EndPath はクリップ確定時のみエントリ）
// - Letter.TextSequence はテキスト表示オペレータ単位の 1 始まり連番
//
// 使い方: dotnet run --project tests/tools/pdf-emf/IrMapProbe/App -- <入力.pdf> <出力.svg>
//             [--keep-bg] [--crop x0 y0 x1 y1] [--emf 出力.emf] [--dump-order 順序.json]
//         診断ログは <出力.svg>.diag.json へ併記する
// --keep-bg:     背景矩形の除去（設計書§4.3、既定で有効）を無効化する
// --crop:        IR座標（mm、ページ左上原点・Y下向き）の矩形で viewBox クロップする（FR-4予行）
// --emf:         同じ IR からクラシックGDIのEMFを出力する（Phase 0「SVG IR→EMF 接続」予行）
// --dump-order:  ir.Nodes の kind 列を出現順で JSON 出力（描画順の機械照合。--keep-bg 併用推奨）
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CadPeta.Ir;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Graphics.Colors;
using UglyToad.PdfPig.Graphics.Operations;
using UglyToad.PdfPig.Graphics.Operations.ClippingPaths;
using UglyToad.PdfPig.Graphics.Operations.General;
using UglyToad.PdfPig.Graphics.Operations.InlineImages;
using UglyToad.PdfPig.Graphics.Operations.MarkedContent;
using UglyToad.PdfPig.Graphics.Operations.PathPainting;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Graphics.Operations.TextShowing;
using UglyToad.PdfPig.Graphics.Operations.TextState;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Graphics;
using UglyToad.PdfPig.Logging;
using UglyToad.PdfPig.Parser;
using UglyToad.PdfPig.Tokenization.Scanner;
using UglyToad.PdfPig.Tokens;

const double PT2MM = 25.4 / 72.0;
// SVG に PDF の「線幅0=デバイス最細線」の概念がないため、最細線は固定幅へ置換する
// （非可逆の正規化として診断ログに記録する。値はコンテンツ量子 0.03pt の約5倍）
const double HairlineMm = 0.05;

if (args.Length < 2)
{
    Console.Error.WriteLine("使い方: IrMapProbe <入力.pdf> <出力.svg> [--keep-bg] [--crop x0 y0 x1 y1] [--emf out.emf] [--dump-order order.json]");
    return 1;
}
bool keepBg = args.Contains("--keep-bg");
string? emfOut = null;
int emfAt = Array.IndexOf(args, "--emf");
if (emfAt >= 0)
{
    if (emfAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("--emf には出力パスが必要");
        return 1;
    }
    emfOut = args[emfAt + 1];
}
// --dump-order: ir.Nodes の kind 列（path/text/image）を出現順で JSON 出力する。
// 描画順の機械照合（compare_draworder.py）で、背景除去前の順序を dissect と揃えるため、
// このダンプは --keep-bg と併用して呼ぶ想定（背景矩形も列に含める）。
string? dumpOrder = null;
int dumpAt = Array.IndexOf(args, "--dump-order");
if (dumpAt >= 0)
{
    if (dumpAt + 1 >= args.Length)
    {
        Console.Error.WriteLine("--dump-order には出力パスが必要");
        return 1;
    }
    dumpOrder = args[dumpAt + 1];
}
double[]? crop = null;
int cropAt = Array.IndexOf(args, "--crop");
if (cropAt >= 0)
{
    if (cropAt + 4 >= args.Length)
    {
        Console.Error.WriteLine("--crop には x0 y0 x1 y1（mm）が必要");
        return 1;
    }
    crop = args.Skip(cropAt + 1).Take(4)
               .Select(a => double.Parse(a, CultureInfo.InvariantCulture)).ToArray();
}

var diag = new IrDiagnostics();
var ir = new IrDocument();

using (var doc = PdfDocument.Open(args[0]))
{
    var page = doc.GetPage(1);
    ir.WidthMm = page.Width * PT2MM;
    ir.HeightMm = page.Height * PT2MM;
    Console.WriteLine($"page: {page.Width} x {page.Height} pt = " +
                      $"{ir.WidthMm:F3} x {ir.HeightMm:F3} mm");

    // 座標変換: pt（PDF、Y上向き）→ mm（IR、Y下向き。設計書§2.2）
    double Xmm(double xPt) => xPt * PT2MM;
    double Ymm(double yPt) => ir.HeightMm - yPt * PT2MM;

    // ---------- 間接参照の解決子とページリソース（PdfPigProbe 流用） ----------
    IToken Resolve(IToken t) =>
        t is IndirectReferenceToken r ? doc.Structure.GetObject(r.Data).Data : t;

    DictionaryToken? DictOf(IToken? t) =>
        t == null ? null : Resolve(t) as DictionaryToken;

    DictionaryToken? resources = page.Dictionary.TryGet(NameToken.Create("Resources"), out var resTok)
        ? DictOf(resTok) : null;

    double GsNum(DictionaryToken d, string key, double fallback)
        => d.TryGet(NameToken.Create(key), out var v) && Resolve(v) is NumericToken n
            ? (double)n.Data : fallback;

    // /Do の XObject ストリームと /Subtype を返す（Form 再帰のため本体 PdfIrAcquirer と同一）。
    // ExtGState/Properties/XObject は Form ごとに固有リソースを引くため、走査文脈の
    // currentResources から都度解決する。
    bool TryGetXObject(DictionaryToken? currentResources, NameToken name,
                       out StreamToken? stream, out string? subtype)
    {
        stream = null;
        subtype = null;
        DictionaryToken? xObjects = currentResources != null
            && currentResources.TryGet(NameToken.Create("XObject"), out var xoTok)
            ? DictOf(xoTok) : null;
        if (xObjects == null || !xObjects.TryGet(name, out var token)
            || Resolve(token) is not StreamToken found
            || !found.StreamDictionary.TryGet(NameToken.Create("Subtype"), out var subtypeToken)
            || Resolve(subtypeToken) is not NameToken subtypeName)
            return false;
        stream = found;
        subtype = subtypeName.Data;
        return true;
    }

    // ---------- 状態機械（PdfPigProbe 流用 + Tz・クリップ追跡を拡張） ----------
    var ctm = new double[] { 1, 0, 0, 1, 0, 0 };
    double lw = 1.0, ca = 1.0, CA = 1.0, tz = 100.0;
    string? clip = null;
    var stack = new Stack<(double[] ctm, double lw, double ca, double CA, double tz, string? clip)>();
    var layerStack = new Stack<string?>();
    bool clipPending = false, clipEvenOdd = false;
    int clipCounter = 0;
    int drawOrder = 0;             // 描画順（内容ストリーム順）の通し番号
    bool hasUnresolvedXObject = false;

    static double[] MatMul(double[] a, double[] b) => new[]
    {
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5],
    };

    // Page.Paths と同順のスナップショット。establishClip はこのエントリがクリップを確定
    // する場合の新規クリップ id（クリップは後続の描画から効く。確定パス自身には旧クリップ）
    var pathStates = new List<(string op, double lwDevPt, double ca, double CA,
                               string? layer, string? clip, string? establishClip, bool clipEO,
                               int order)>();
    var textStates = new List<(double ca, string? layer, string? clip, double tz, int order)>();
    var imageStates = new List<(double ca, string? layer, string? clip, int order)>();
    var formParser = new PageContentParser(ReflectionGraphicsStateOperationFactory.Instance,
                                           new StackDepthGuard(256), false);
    var filterProvider = new LookupFilterProvider();
    var activeForms = new HashSet<StreamToken>();
    const int MaxFormDepth = 64;

    // 内容ストリームを depth-first に走査する（Form XObject は固有リソース・Matrix を反映して再帰）。
    void ScanOperations(IReadOnlyList<IGraphicsStateOperation> operations,
                        DictionaryToken? currentResources, int formDepth)
    {
      foreach (var op in operations)
      {
        switch (op)
        {
            case Push:
                stack.Push((ctm.ToArray(), lw, ca, CA, tz, clip));
                break;
            case Pop:
                if (stack.Count > 0) (ctm, lw, ca, CA, tz, clip) = stack.Pop();
                break;
            case ModifyCurrentTransformationMatrix m:
                ctm = MatMul(m.Value, ctm);
                break;
            case SetLineWidth w_:
                lw = (double)w_.Width;
                break;
            case SetHorizontalScaling hs:
                tz = (double)hs.Scale;
                break;
            case SetGraphicsStateParametersFromDictionary gs:
                var extGStates = currentResources != null
                    && currentResources.TryGet(NameToken.Create("ExtGState"), out var egsTok)
                    ? DictOf(egsTok) : null;
                if (extGStates != null
                    && extGStates.TryGet(gs.Name, out var egTok)
                    && DictOf(egTok) is { } eg)
                {
                    ca = GsNum(eg, "ca", ca);
                    CA = GsNum(eg, "CA", CA);
                    lw = GsNum(eg, "LW", lw);
                }
                break;
            case BeginMarkedContentWithProperties bdc:
            {
                var mcProperties = currentResources != null
                    && currentResources.TryGet(NameToken.Create("Properties"), out var propTok)
                    ? DictOf(propTok) : null;
                string? layer = null;
                if (bdc.Name.Data == "OC")
                {
                    DictionaryToken? props = bdc.Properties;
                    if (props == null && bdc.PropertyDictionaryName != null && mcProperties != null
                        && mcProperties.TryGet(bdc.PropertyDictionaryName, out var pTok))
                        props = DictOf(pTok);
                    if (props != null && props.TryGet(NameToken.Create("Name"), out var nTok)
                        && Resolve(nTok) is IDataToken<string> s)
                        layer = s.Data;
                }
                layerStack.Push(layer);
                break;
            }
            case BeginMarkedContent:
                layerStack.Push(null);
                break;
            case EndMarkedContent:
                if (layerStack.Count > 0) layerStack.Pop();
                break;
            case ModifyClippingByEvenOddIntersect:
                clipPending = true; clipEvenOdd = true;
                break;
            case ModifyClippingByNonZeroWindingIntersect:
                clipPending = true; clipEvenOdd = false;
                break;

            case StrokePath: case CloseAndStrokePath:
            case FillPathEvenOddRule: case FillPathNonZeroWinding:
            case FillPathNonZeroWindingCompatibility:
            case FillPathEvenOddRuleAndStroke: case FillPathNonZeroWindingAndStroke:
            case CloseFillPathEvenOddRuleAndStroke: case CloseFillPathNonZeroWindingAndStroke:
            case EndPath:
            {
                double scale = Math.Sqrt(Math.Abs(ctm[0] * ctm[3] - ctm[1] * ctm[2]));
                bool isEndPath = op is EndPath;
                string? establish = null;
                if (clipPending)
                {
                    if (clip != null)
                        diag.Add("入れ子クリップ（交差合成は未実装、内側のみ適用）",
                                 $"clip{clipCounter} の確定時に {clip} が有効");
                    establish = $"clip{clipCounter++}";
                }
                if (!isEndPath || clipPending)
                    pathStates.Add((op.Operator, lw * scale, ca, CA,
                                    layerStack.Count > 0 ? layerStack.Peek() : null,
                                    clip, establish, clipEvenOdd, drawOrder++));
                if (establish != null) clip = establish;
                clipPending = false;
                break;
            }

            case ShowText: case ShowTextsWithPositioning:
                textStates.Add((ca, layerStack.Count > 0 ? layerStack.Peek() : null, clip, tz,
                                drawOrder++));
                break;

            case InvokeNamedXObject xobj:
                if (TryGetXObject(currentResources, xobj.Name, out var xstream, out var subtype))
                {
                    if (subtype == "Image")
                        imageStates.Add((ca, layerStack.Count > 0 ? layerStack.Peek() : null,
                                         clip, drawOrder++));
                    else if (subtype == "Form" && xstream != null)
                    {
                        if (formDepth >= MaxFormDepth || !activeForms.Add(xstream))
                        {
                            hasUnresolvedXObject = true;
                            diag.Add("プロファイル外: Form XObject の循環・深さ超過（描画対応を確定不能）",
                                     xobj.Name.Data);
                            break;
                        }
                        var saved = (ctm.ToArray(), lw, ca, CA, tz, clip, clipPending, clipEvenOdd);
                        var savedStack = stack.ToArray();
                        var savedLayers = layerStack.ToArray();
                        try
                        {
                            if (xstream.StreamDictionary.TryGet(NameToken.Create("Matrix"), out var matrixTok)
                                && Resolve(matrixTok) is ArrayToken matrix && matrix.Data.Count == 6
                                && matrix.Data.Select(Resolve).All(t => t is NumericToken))
                            {
                                var values = matrix.Data.Select(t => (double)((NumericToken)Resolve(t)).Data).ToArray();
                                ctm = MatMul(values, ctm);
                            }
                            DictionaryToken? formResources = xstream.StreamDictionary.TryGet(
                                NameToken.Create("Resources"), out var formResTok)
                                ? DictOf(formResTok) : currentResources;
                            var bytes = xstream.Decode(filterProvider, doc.Structure.TokenScanner);
                            var formOps = formParser.Parse(page.Number,
                                new MemoryInputBytes(bytes), new SilentLog());
                            ScanOperations(formOps, formResources, formDepth + 1);
                        }
                        catch (Exception ex)
                        {
                            hasUnresolvedXObject = true;
                            diag.Add("プロファイル外: Form XObject の内容解析失敗（描画対応を確定不能）",
                                     $"{xobj.Name.Data}: {ex.Message}");
                        }
                        finally
                        {
                            activeForms.Remove(xstream);
                            (ctm, lw, ca, CA, tz, clip, clipPending, clipEvenOdd) = saved;
                            stack.Clear();
                            for (int i = savedStack.Length - 1; i >= 0; i--) stack.Push(savedStack[i]);
                            layerStack.Clear();
                            for (int i = savedLayers.Length - 1; i >= 0; i--) layerStack.Push(savedLayers[i]);
                        }
                    }
                }
                else
                {
                    hasUnresolvedXObject = true;
                    diag.Add("プロファイル外: XObject の Subtype を解決できず（画像対応を確定不能）",
                             xobj.Name.Data);
                }
                break;
            case BeginInlineImage:
                imageStates.Add((ca, layerStack.Count > 0 ? layerStack.Peek() : null, clip,
                                 drawOrder++));
                break;
        }
      }
    }

    ScanOperations(page.Operations, resources, 0);

    if (pathStates.Count != page.Paths.Count)
        Console.Error.WriteLine(
            $"警告: 状態数 {pathStates.Count} != Paths {page.Paths.Count}（1:1前提が崩れている）");

    int textRunCount = page.Letters.Select(l => l.TextSequence).Distinct().Count();
    bool drawingAligned = !hasUnresolvedXObject
                          && pathStates.Count == page.Paths.Count
                          && textStates.Count == textRunCount;
    if (!drawingAligned)
        diag.Add("プロファイル外: 描画操作と抽出要素の対応を確定不能。" +
                 "パス・文字・画像の z順/属性を安全な既定値へフォールバック",
                 $"pathStates={pathStates.Count} Paths={page.Paths.Count} " +
                 $"textStates={textStates.Count} textRuns={textRunCount} " +
                 $"unresolvedXObject={hasUnresolvedXObject}");

    var images = page.GetImages().ToList();
    bool imagesAligned = drawingAligned && imageStates.Count == images.Count;
    if (!imagesAligned)
        diag.Add("プロファイル外: 画像の操作・抽出対応を確定不能（件数不一致・XObject解決失敗・Form入れ子等）。" +
                 "z順・レイヤ/クリップ/透過の対応を確定できず末尾へ近似配置",
                 $"imageStates={imageStates.Count} GetImages={images.Count} " +
                 $"unresolvedXObject={hasUnresolvedXObject}");

    // 型別3ループの全ノードを (描画順, ノード) で集め、最後に安定ソートして真の z順へ復元する。
    var tagged = new List<(int order, IrNode node)>();

    // ---------- パス幾何の変換（ベジェ制御点保持・mm・Y反転） ----------
    (List<IrSegment> segs, double[] bboxMm) ToSegments(IReadOnlyList<PdfSubpath> subpaths)
    {
        var segs = new List<IrSegment>();
        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;
        PdfPoint? cur = null;
        void Upd(double x, double y)
        {
            minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
            miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
        }
        void Emit(IrSegment s, double x, double y) { segs.Add(s); Upd(x, y); }
        foreach (var sp in subpaths)
        {
            foreach (var cmd in sp.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move m:
                        Emit(new IrMove(Xmm(m.Location.X), Ymm(m.Location.Y)),
                             Xmm(m.Location.X), Ymm(m.Location.Y));
                        cur = m.Location;
                        break;
                    case PdfSubpath.Line l:
                        if (cur == null || Math.Abs(cur.Value.X - l.From.X) > 1e-6
                                        || Math.Abs(cur.Value.Y - l.From.Y) > 1e-6)
                            Emit(new IrMove(Xmm(l.From.X), Ymm(l.From.Y)),
                                 Xmm(l.From.X), Ymm(l.From.Y));
                        Emit(new IrLine(Xmm(l.To.X), Ymm(l.To.Y)), Xmm(l.To.X), Ymm(l.To.Y));
                        cur = l.To;
                        break;
                    case PdfSubpath.CubicBezierCurve b:
                        segs.Add(new IrCubic(
                            Xmm(b.FirstControlPoint.X), Ymm(b.FirstControlPoint.Y),
                            Xmm(b.SecondControlPoint.X), Ymm(b.SecondControlPoint.Y),
                            Xmm(b.EndPoint.X), Ymm(b.EndPoint.Y)));
                        Upd(Xmm(b.FirstControlPoint.X), Ymm(b.FirstControlPoint.Y));
                        Upd(Xmm(b.SecondControlPoint.X), Ymm(b.SecondControlPoint.Y));
                        Upd(Xmm(b.EndPoint.X), Ymm(b.EndPoint.Y));
                        cur = b.EndPoint;
                        break;
                    case PdfSubpath.QuadraticBezierCurve q:
                    {
                        // 2次→3次ベジェ昇格（制御点 1/3・2/3 内分）。コーパスには出現しない想定
                        var p0 = cur ?? q.ControlPoint;
                        double c1x = p0.X + 2.0 / 3.0 * (q.ControlPoint.X - p0.X);
                        double c1y = p0.Y + 2.0 / 3.0 * (q.ControlPoint.Y - p0.Y);
                        double c2x = q.EndPoint.X + 2.0 / 3.0 * (q.ControlPoint.X - q.EndPoint.X);
                        double c2y = q.EndPoint.Y + 2.0 / 3.0 * (q.ControlPoint.Y - q.EndPoint.Y);
                        segs.Add(new IrCubic(Xmm(c1x), Ymm(c1y), Xmm(c2x), Ymm(c2y),
                                             Xmm(q.EndPoint.X), Ymm(q.EndPoint.Y)));
                        Upd(Xmm(q.EndPoint.X), Ymm(q.EndPoint.Y));
                        cur = q.EndPoint;
                        break;
                    }
                    case PdfSubpath.Close:
                        segs.Add(new IrClose());
                        break;
                }
            }
        }
        return (segs, minx <= maxx ? new[] { minx, miny, maxx, maxy } : new[] { 0.0, 0, 0, 0 });
    }

    static string BboxStr(double[] b) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"bbox_mm=({b[0]:F1},{b[1]:F1})-({b[2]:F1},{b[3]:F1})");

    static IrRgb ToIr(IColor c)
    {
        var (r, g, b) = c.ToRGBValues();
        return new IrRgb((double)r, (double)g, (double)b);
    }

    // ---------- パス → IrPath / IrClipDef ----------
    int pathIdx = 0;
    foreach (var p in page.Paths)
    {
        var st = drawingAligned && pathIdx < pathStates.Count
            ? pathStates[pathIdx]
            : (op: "", lwDevPt: 1.0, ca: 1.0, CA: 1.0, layer: (string?)null,
               clip: (string?)null, establishClip: (string?)null, clipEO: false,
               order: int.MaxValue);
        var (segs, bbox) = ToSegments(p);

        if (st.establishClip != null)
            ir.ClipDefs.Add(new IrClipDef { Id = st.establishClip, Segments = segs, EvenOdd = st.clipEO });

        if (!p.IsClipping)
        {
            var node = new IrPath
            {
                Segments = segs,
                FillEvenOdd = p.FillingRule == UglyToad.PdfPig.Core.FillingRule.EvenOdd,
                FillOpacity = st.ca,
                StrokeOpacity = st.CA,
                DataLayer = st.layer,
                ClipId = st.clip,
            };
            if (p.IsFilled)
            {
                if (p.FillColor == null)
                    diag.Add("既定色の補完（色未設定→黒）", $"path seq={pathIdx} fill {BboxStr(bbox)}");
                node.Fill = p.FillColor != null ? ToIr(p.FillColor) : new IrRgb(0, 0, 0);
            }
            if (p.IsStroked)
            {
                if (p.StrokeColor == null)
                    diag.Add("既定色の補完（色未設定→黒）", $"path seq={pathIdx} stroke {BboxStr(bbox)}");
                node.Stroke = p.StrokeColor != null ? ToIr(p.StrokeColor) : new IrRgb(0, 0, 0);
                double lwMm = st.lwDevPt * PT2MM;
                if (lwMm <= 0)
                {
                    diag.Add($"最細線の固定幅置換（線幅0→{HairlineMm}mm）",
                             $"path seq={pathIdx} {BboxStr(bbox)}");
                    lwMm = HairlineMm;
                }
                node.StrokeWidthMm = lwMm;
            }
            int order = drawingAligned && pathIdx < pathStates.Count
                ? pathStates[pathIdx].order : int.MaxValue;
            tagged.Add((order, node));
        }
        pathIdx++;
    }

    // ---------- 文字 → IrText（テキスト表示オペレータ単位のラン） ----------
    var fontMap = new Dictionary<string, string>
    {
        ["ArialMT"] = "Arial",
        ["Arial-BoldMT"] = "Arial",
        ["Arial-ItalicMT"] = "Arial",
        ["MS-Gothic"] = "MS Gothic",
        ["MS-PGothic"] = "MS PGothic",
        ["MS-Mincho"] = "MS Mincho",
    };
    var unknownFonts = new HashSet<string>();
    string MapFont(string raw)
    {
        var name = Regex.Replace(raw, @"^[A-Z]{6}\+", "");  // サブセット接頭辞を除去
        if (fontMap.TryGetValue(name, out var mapped)) return mapped;
        unknownFonts.Add(name);
        return name;
    }

    int rotatedRuns = 0;
    foreach (var run in page.Letters.GroupBy(l => l.TextSequence).OrderBy(g => g.Key))
    {
        var letters = run.ToList();
        var first = letters[0];
        var ts = drawingAligned && run.Key >= 1 && run.Key <= textStates.Count
            ? textStates[run.Key - 1]
            : (ca: 1.0, layer: (string?)null, clip: (string?)null, tz: 100.0, order: int.MaxValue);
        if (first.Color == null)
            diag.Add("既定色の補完（色未設定→黒）", $"text seq={run.Key} '{first.Value}'");

        // 回転文字（実物図面の縦寸法値で実測した類型）: ベースライン方向から回転角を得る。
        // PDF（Y上向き）の角度を IR（Y下向き、時計回り正）の rotate へ符号反転で写す
        var lastL = letters[^1];
        double bx = lastL.EndBaseLine.X - first.StartBaseLine.X;
        double by = lastL.EndBaseLine.Y - first.StartBaseLine.Y;
        double rotDeg = 0.0;
        if (bx * bx + by * by > 1e-12)
        {
            double a = Math.Atan2(by, bx) * 180.0 / Math.PI;
            if (Math.Abs(a) > 0.01) { rotDeg = -a; rotatedRuns++; }
        }

        // グリフ位置: 水平なら X 絶対値、回転時はベースライン単位ベクトルへの射影距離を
        // XMm 起点の値として持つ（SvgWriter は XMm からの相対値で x 列を作る）
        double[]? glyphXs = null;
        if (letters.Count > 1)
        {
            if (rotDeg == 0.0)
                glyphXs = letters.Select(l => Xmm(l.StartBaseLine.X)).ToArray();
            else
            {
                double rad = Math.Atan2(by, bx);
                double ux = Math.Cos(rad), uy = Math.Sin(rad);
                glyphXs = letters.Select(l =>
                    Xmm(first.StartBaseLine.X)
                    + ((l.StartBaseLine.X - first.StartBaseLine.X) * ux
                       + (l.StartBaseLine.Y - first.StartBaseLine.Y) * uy) * PT2MM).ToArray();
            }
        }

        tagged.Add((ts.order, new IrText
        {
            Value = string.Concat(letters.Select(l => l.Value)),
            XMm = Xmm(first.StartBaseLine.X),
            YMm = Ymm(first.StartBaseLine.Y),
            GlyphXsMm = glyphXs,
            FontFamily = MapFont(first.FontName ?? ""),
            FontSizeMm = first.PointSize * PT2MM,
            WidthFactor = ts.tz / 100.0,
            RotationDeg = rotDeg,
            Fill = first.Color != null ? ToIr(first.Color) : new IrRgb(0, 0, 0),
            FillOpacity = ts.ca,
            DataLayer = ts.layer,
            ClipId = ts.clip,
        }));
    }
    if (rotatedRuns > 0)
        Console.WriteLine($"回転文字ラン: {rotatedRuns}件（ベースライン方向→rotate 写像）");
    if (unknownFonts.Count > 0)
        Console.WriteLine($"フォント名写像なし（生名のまま出力）: {string.Join(", ", unknownFonts)}");

    // ---------- 画像 → IrImage（JPEG + SMask → PNG 合成） ----------
    int imgIdx = 0;
    foreach (var im in images)
    {
        bool imgHasState = imagesAligned && imgIdx < imageStates.Count;
        var ist = imgHasState ? imageStates[imgIdx] : default;
        int order = imgHasState ? imageStates[imgIdx].order : int.MaxValue;
        var bounds = im.BoundingBox;
        var bboxMm = new[] { Xmm(bounds.Left), Ymm(bounds.Top), Xmm(bounds.Right), Ymm(bounds.Bottom) };
        byte[] png = ComposePng(im, doc, out bool hadSMask);
        tagged.Add((order, new IrImage
        {
            XMm = Xmm(bounds.Left),
            YMm = Ymm(bounds.Top),
            WidthMm = (bounds.Right - bounds.Left) * PT2MM,
            HeightMm = (bounds.Top - bounds.Bottom) * PT2MM,
            PngBytes = png,
            DataLayer = ist.layer,
            ClipId = ist.clip,
        }));
        if (hadSMask)
            diag.Add("SOLID×透過の画像化を継承（SMask合成PNGの埋め込み、非可逆JPEG由来）",
                     $"image seq={imgIdx} {im.WidthInSamples}x{im.HeightInSamples}px {BboxStr(bboxMm)}");
        if (imgHasState && ist.ca < 1.0)
            diag.Add("プロファイル外: 画像への ca 透過（未表現）", $"image seq={imgIdx} ca={ist.ca}");
        imgIdx++;
    }

    // 型別3ループの全ノードを描画順で安定ソートして ir.Nodes へ確定する。
    foreach (var (_, node) in tagged.OrderBy(t => t.order))
        ir.Nodes.Add(node);

    // ---------- SHX 注釈（EPDFSHX、あれば補助情報として記録） ----------
    int annots = 0;
    foreach (var a in page.GetAnnotations())
    {
        annots++;
        diag.Add("SHX輪郭化の元文字列注釈（回収のみ・IR未反映）",
                 $"'{a.Content}' rect_pt=({a.Rectangle.Left:F1},{a.Rectangle.Bottom:F1})");
    }

    Console.WriteLine($"IR: paths={ir.Nodes.OfType<IrPath>().Count()} " +
                      $"texts={ir.Nodes.OfType<IrText>().Count()} " +
                      $"images={ir.Nodes.OfType<IrImage>().Count()} " +
                      $"clips={ir.ClipDefs.Count} annots={annots}");
}

// ---------- クリップ定義の同一幾何の集約 ----------
{
    var canonical = new Dictionary<string, string>();  // d文字列 → 代表id
    var remap = new Dictionary<string, string>();      // 旧id → 代表id
    var kept = new List<IrClipDef>();
    foreach (var c in ir.ClipDefs)
    {
        var key = string.Join("|", c.Segments.Select(s => s.ToString())) + (c.EvenOdd ? "|eo" : "");
        if (canonical.TryGetValue(key, out var rep))
            remap[c.Id] = rep;
        else
        {
            canonical[key] = c.Id;
            remap[c.Id] = c.Id;
            kept.Add(c);
        }
    }
    ir.ClipDefs = kept;
    foreach (var n in ir.Nodes)
        if (n.ClipId != null) n.ClipId = remap[n.ClipId];
    Console.WriteLine($"クリップ集約: {remap.Count} → {kept.Count}");
}

// ---------- 背景矩形の除去（設計書§4.3: 三条件で同定し既定で除去、FR-4） ----------
// 三条件: ①描画順が最初期（先頭からの連続のみ対象）②ページ範囲とほぼ一致 ③塗りのみ
if (!keepBg)
{
    int bgRemoved = 0;
    while (ir.Nodes.Count > 0
           && ir.Nodes[0] is IrPath bg && bg.Fill != null && bg.Stroke == null
           && CoversPage(SegsBbox(bg.Segments), ir.WidthMm, ir.HeightMm))
    {
        diag.Add("背景矩形の除去（描画順最初期・ページ範囲とほぼ一致・塗りのみ）",
                 $"node seq=0 fill={bg.Fill.Value.ToSvg()} {BboxText(SegsBbox(bg.Segments))}");
        ir.Nodes.RemoveAt(0);
        bgRemoved++;
    }
    Console.WriteLine($"背景除去: {bgRemoved}件");
}

// ---------- viewBox クロップ（任意mm矩形の切り出し、FR-4/FR-6 予行） ----------
// ルート svg はビューポート外を描画しないため、viewBox の差し替えだけで切り出しになる。
// width/height はクロップ範囲の実寸（mm）となり、貼り付け時の寸法根拠（FR-6）を保つ
if (crop != null)
{
    ir.ViewMinXMm = crop[0];
    ir.ViewMinYMm = crop[1];
    ir.WidthMm = crop[2] - crop[0];
    ir.HeightMm = crop[3] - crop[1];
    Console.WriteLine($"クロップ: ({crop[0]},{crop[1]})-({crop[2]},{crop[3]})mm " +
                      $"→ width={ir.WidthMm:F3}mm height={ir.HeightMm:F3}mm");
}

// ---------- 描画順ダンプ（機械照合用、任意） ----------
if (dumpOrder != null)
{
    // PDF印刷経路（PDF取得）は IrGroup を生まない（path/text/image のみ）。
    var kinds = ir.Nodes.Select(n => n switch
    {
        IrPath => "path",
        IrText => "text",
        IrImage => "image",
        _ => "other",
    }).ToList();
    File.WriteAllText(dumpOrder,
        JsonSerializer.Serialize(new { source = args[0], kinds },
            new JsonSerializerOptions { WriteIndented = true }),
        new UTF8Encoding(false));
    Console.WriteLine($"描画順ダンプ出力: {dumpOrder}（{kinds.Count} ノード）");
}

// ---------- 出力（SVG + 診断ログ） ----------
File.WriteAllText(args[1], SvgWriter.Write(ir), new UTF8Encoding(false));
Console.WriteLine($"SVG出力: {args[1]}");

// ---------- EMF出力（クラシックGDI、事前クリップ・透過劣化の診断ログは同じ diag へ） ----------
if (emfOut != null)
{
    var swEmf = System.Diagnostics.Stopwatch.StartNew();
    CadPeta.EmfOut.EmfWriter.Write(ir, emfOut, diag);
    swEmf.Stop();
    Console.WriteLine($"EMF出力: {emfOut} " +
                      $"({new FileInfo(emfOut).Length:N0} bytes, {swEmf.ElapsedMilliseconds}ms)");
}

var diagOut = new List<object>();
Console.WriteLine("---- 診断ログ（設計書§7: 件数と対象） ----");
foreach (var (type, count, targets) in diag.Summarize())
{
    Console.WriteLine($"{type}: {count}件");
    diagOut.Add(new { type, count, targets });
}
if (diag.Entries.Count == 0) Console.WriteLine("（記録なし）");
File.WriteAllText(args[1] + ".diag.json",
    JsonSerializer.Serialize(diagOut, new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    }), new UTF8Encoding(false));
Console.WriteLine($"診断ログ出力: {args[1]}.diag.json");
return 0;

// ---------- 背景矩形判定の幾何ヘルパ ----------
static double[] SegsBbox(IEnumerable<IrSegment> segs)
{
    double minx = double.MaxValue, miny = double.MaxValue;
    double maxx = double.MinValue, maxy = double.MinValue;
    void Upd(double x, double y)
    {
        minx = Math.Min(minx, x); maxx = Math.Max(maxx, x);
        miny = Math.Min(miny, y); maxy = Math.Max(maxy, y);
    }
    foreach (var s in segs)
        switch (s)
        {
            case IrMove m: Upd(m.X, m.Y); break;
            case IrLine l: Upd(l.X, l.Y); break;
            case IrCubic c: Upd(c.X1, c.Y1); Upd(c.X2, c.Y2); Upd(c.X, c.Y); break;
        }
    return minx <= maxx ? new[] { minx, miny, maxx, maxy } : new[] { 0.0, 0, 0, 0 };
}

static string BboxText(double[] b) =>
    string.Create(CultureInfo.InvariantCulture,
                  $"bbox_mm=({b[0]:F1},{b[1]:F1})-({b[2]:F1},{b[3]:F1})");

// 「ページ範囲とほぼ一致」の判定。各辺がページ縁の ±2mm 以内にあること
static bool CoversPage(double[] b, double wMm, double hMm) =>
    Math.Abs(b[0]) <= 2.0 && Math.Abs(b[1]) <= 2.0
    && Math.Abs(b[2] - wMm) <= 2.0 && Math.Abs(b[3] - hMm) <= 2.0;

// ---------- 画像デコード（Flate/ASCII85 は自前、JPEG のみ System.Drawing） ----------
static byte[] ComposePng(UglyToad.PdfPig.Content.IPdfImage im, PdfDocument doc, out bool hadSMask)
{
    var dict = im.ImageDictionary;
    byte[] color = ApplyFilters(im.RawBytes.ToArray(), dict, doc, out bool colorIsJpeg);
    using var colorBmp = colorIsJpeg
        ? new Bitmap(new MemoryStream(color))
        : GrayToBitmap(color, im.WidthInSamples, im.HeightInSamples);

    hadSMask = false;
    byte[]? alpha = null;
    int aw = 0, ah = 0;
    // SMask は間接参照のことも解決済み StreamToken のこともある。両対応で読む
    StreamToken? sm = null;
    if (dict.TryGet(NameToken.Create("SMask"), out var smTok))
        sm = smTok as StreamToken
             ?? (smTok is IndirectReferenceToken smRef
                 ? doc.Structure.GetObject(smRef.Data).Data as StreamToken : null);
    else
        Console.WriteLine($"ImageDictionary keys: {string.Join(", ", dict.Data.Keys)}");
    if (sm != null)
    {
        hadSMask = true;
        alpha = ApplyFilters(sm.Data.ToArray(), sm.StreamDictionary, doc, out bool aJpeg);
        if (aJpeg) throw new NotSupportedException("SMask が DCT（未対応）");
        aw = IntOf(sm.StreamDictionary, "Width");
        ah = IntOf(sm.StreamDictionary, "Height");
    }

    int w = colorBmp.Width, h = colorBmp.Height;
    using var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(outBmp))
        g.DrawImage(colorBmp, 0, 0, w, h);

    if (alpha != null)
    {
        var data = outBmp.LockBits(new Rectangle(0, 0, w, h),
                                   ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                int sy = ah == h ? y : y * ah / h;
                for (int x = 0; x < w; x++)
                {
                    int sx = aw == w ? x : x * aw / w;
                    row[x * 4 + 3] = alpha[sy * aw + sx];
                }
            }
        }
        outBmp.UnlockBits(data);
    }

    using var ms = new MemoryStream();
    outBmp.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static int IntOf(DictionaryToken d, string key)
    => d.TryGet(NameToken.Create(key), out var v) && v is NumericToken n ? n.Int : 0;

static Bitmap GrayToBitmap(byte[] gray, int w, int h)
{
    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
    unsafe
    {
        for (int y = 0; y < h; y++)
        {
            byte* row = (byte*)data.Scan0 + y * data.Stride;
            for (int x = 0; x < w; x++)
            {
                byte v = gray[y * w + x];
                row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = 255;
            }
        }
    }
    bmp.UnlockBits(data);
    return bmp;
}

// /Filter 配列の順にフィルタを適用する。DCTDecode は終端でのみ許し、JPEG のまま返す
static byte[] ApplyFilters(byte[] data, DictionaryToken dict, PdfDocument doc, out bool isJpeg)
{
    isJpeg = false;
    if (!dict.TryGet(NameToken.Create("Filter"), out var fTok)) return data;
    var names = fTok switch
    {
        NameToken n => new List<string> { n.Data },
        ArrayToken arr => arr.Data.OfType<NameToken>().Select(n => n.Data).ToList(),
        _ => new List<string>(),
    };
    foreach (var f in names)
    {
        switch (f)
        {
            case "FlateDecode":
                using (var zs = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress))
                using (var ms = new MemoryStream())
                {
                    zs.CopyTo(ms);
                    data = ms.ToArray();
                }
                break;
            case "ASCII85Decode":
                data = Ascii85Decode(data);
                break;
            case "DCTDecode":
                isJpeg = true;
                return data;  // JPEG は System.Drawing に委ねる
            default:
                throw new NotSupportedException($"未対応フィルタ: {f}");
        }
    }
    return data;
}

static byte[] Ascii85Decode(byte[] input)
{
    var output = new List<byte>(input.Length * 4 / 5);
    uint tuple = 0;
    int count = 0;
    for (int i = 0; i < input.Length; i++)
    {
        char c = (char)input[i];
        if (c is ' ' or '\r' or '\n' or '\t' or '\0') continue;
        if (c == '~') break;  // 終端 ~>
        if (c == 'z' && count == 0)
        {
            output.AddRange(new byte[4]);
            continue;
        }
        if (c is < '!' or > 'u') throw new FormatException($"ASCII85範囲外: '{c}'");
        tuple = tuple * 85 + (uint)(c - '!');
        if (++count == 5)
        {
            output.Add((byte)(tuple >> 24)); output.Add((byte)(tuple >> 16));
            output.Add((byte)(tuple >> 8)); output.Add((byte)tuple);
            tuple = 0; count = 0;
        }
    }
    if (count > 0)  // 端数グループ: 'u' でパディングし (count-1) バイト取り出す
    {
        for (int i = count; i < 5; i++) tuple = tuple * 85 + 84;
        for (int i = 0; i < count - 1; i++) output.Add((byte)(tuple >> (24 - i * 8)));
    }
    return output.ToArray();
}

// Form XObject の内容ストリーム解析に使う補助（本体 PdfIrAcquirer と同一）。
sealed class SilentLog : ILog
{
    public void Debug(string message) { }
    public void Debug(string message, Exception ex) { }
    public void Warn(string message) { }
    public void Error(string message) { }
    public void Error(string message, Exception ex) { }
}

sealed class LookupFilterProvider : ILookupFilterProvider
{
    readonly IFilterProvider inner = DefaultFilterProvider.Instance;

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary) =>
        inner.GetFilters(dictionary);

    public IReadOnlyList<IFilter> GetNamedFilters(IReadOnlyList<NameToken> names) =>
        inner.GetNamedFilters(names);

    public IReadOnlyList<IFilter> GetAllFilters() => inner.GetAllFilters();

    public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary, IPdfTokenScanner scanner)
    {
        IToken ResolveOne(IToken token) => token is IndirectReferenceToken reference
            ? scanner.Get(reference.Data)?.Data ?? token : token;
        var resolved = new DictionaryToken(dictionary.Data.ToDictionary(
            pair => NameToken.Create(pair.Key), pair => ResolveOne(pair.Value)));
        return inner.GetFilters(resolved);
    }
}
