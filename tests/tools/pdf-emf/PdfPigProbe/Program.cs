// PdfPig によるプロットPDFの抽出プローブ（Phase 0 抽出検証・第2段+第3段）
// パス（ベジェ制御点・クリップフラグ・色・線幅）と文字（フォント・Unicode・位置）を
// JSON へ書き出す。セル分類・表出力は Python 側で行う。
//
// 第3段の追加: PdfPig の完成品 API に無い3点（透過 ca/CA・レイヤ /OC 帰属・線幅の CTM 補正）を
// Page.Operations の軽量状態追跡（q/Q/cm/gs/BDC/EMC/w + 描画オペレータ）で補完し、
// 各パス・各文字レコードへ付与する。PDFBox（正解データ生成器）と数値照合するのが目的。
// 使い方: dotnet run --project tests/tools/pdf-emf/PdfPigProbe -- <入力.pdf> <出力.json>
using System.Text.Json;
using UglyToad.PdfPig;
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
using UglyToad.PdfPig.Tokens;

if (args.Length < 2)
{
    Console.Error.WriteLine("使い方: PdfPigProbe <入力.pdf> <出力.json>");
    return 1;
}

static double[]? ColorArr(IColor? c)
{
    if (c == null) return null;
    var (r, g, b) = c.ToRGBValues();
    return new[] { (double)r, (double)g, (double)b };
}

var records = new List<Dictionary<string, object?>>();

using (var doc = PdfDocument.Open(args[0]))
{
    var page = doc.GetPage(1);
    Console.WriteLine($"page size: {page.Width} x {page.Height} pt");

    // ---------- 第3段: Operations の軽量状態追跡 ----------
    // 解決子: 間接参照トークンを実体へ
    IToken Resolve(IToken t) =>
        t is IndirectReferenceToken r ? doc.Structure.GetObject(r.Data).Data : t;

    DictionaryToken? DictOf(IToken? t) =>
        t == null ? null : Resolve(t) as DictionaryToken;

    // ページリソースから ExtGState と Properties(OCG) の辞書を得る
    DictionaryToken? resources = page.Dictionary.TryGet(NameToken.Create("Resources"), out var resTok)
        ? DictOf(resTok) : null;
    DictionaryToken? extGStates = resources != null
        && resources.TryGet(NameToken.Create("ExtGState"), out var egsTok) ? DictOf(egsTok) : null;
    DictionaryToken? mcProperties = resources != null
        && resources.TryGet(NameToken.Create("Properties"), out var propTok) ? DictOf(propTok) : null;

    double GsNum(DictionaryToken d, string key, double fallback)
        => d.TryGet(NameToken.Create(key), out var v) && Resolve(v) is NumericToken n
            ? (double)n.Data : fallback;

    // グラフィックス状態（追跡対象のみ）
    var ctm = new double[] { 1, 0, 0, 1, 0, 0 };
    double lw = 1.0, ca = 1.0, CA = 1.0;
    var stack = new Stack<(double[] ctm, double lw, double ca, double CA)>();
    var layerStack = new Stack<string?>();
    bool clipPending = false;

    static double[] MatMul(double[] a, double[] b) => new[]
    {
        a[0] * b[0] + a[1] * b[2],
        a[0] * b[1] + a[1] * b[3],
        a[2] * b[0] + a[3] * b[2],
        a[2] * b[1] + a[3] * b[3],
        a[4] * b[0] + a[5] * b[2] + b[4],
        a[4] * b[1] + a[5] * b[3] + b[5],
    };

    // 描画オペレータごとの状態スナップショット
    var pathStates = new List<(string op, bool clip, double lwDev, double scale,
                               double ca, double CA, string? layer)>();
    var textStates = new List<(double ca, double CA, string? layer)>();
    var imageStates = new List<(double ca, string? layer)>();

    foreach (var op in page.Operations)
    {
        switch (op)
        {
            case Push:
                stack.Push((ctm.ToArray(), lw, ca, CA));
                break;
            case Pop:
                if (stack.Count > 0) (ctm, lw, ca, CA) = stack.Pop();
                break;
            case ModifyCurrentTransformationMatrix m:
                ctm = MatMul(m.Value, ctm);
                break;
            case SetLineWidth w_:
                lw = (double)w_.Width;
                break;
            case SetGraphicsStateParametersFromDictionary gs:
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
            case ModifyClippingByNonZeroWindingIntersect:
                clipPending = true;
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
                // Page.Paths への出現規則: 描画オペレータは常に1エントリ。
                // EndPath はクリップ確定時のみ IsClipping エントリになる（実測で検証する）
                if (!isEndPath || clipPending)
                    pathStates.Add((op.Operator, clipPending, lw * scale, scale,
                                    ca, CA, layerStack.Count > 0 ? layerStack.Peek() : null));
                clipPending = false;
                break;
            }

            case ShowText: case ShowTextsWithPositioning:
                textStates.Add((ca, CA, layerStack.Count > 0 ? layerStack.Peek() : null));
                break;

            case InvokeNamedXObject:
            case BeginInlineImage:
                imageStates.Add((ca, layerStack.Count > 0 ? layerStack.Peek() : null));
                break;
        }
    }

    Console.WriteLine($"状態追跡: pathStates={pathStates.Count} (Paths={page.Paths.Count}) " +
                      $"textStates={textStates.Count} imageStates={imageStates.Count}");

    // ---- パス ----
    int pathIdx = 0;
    foreach (var p in page.Paths)
    {
        double minx = double.MaxValue, miny = double.MaxValue;
        double maxx = double.MinValue, maxy = double.MinValue;
        int lines = 0, curves = 0, moves = 0, closes = 0;
        double[]? firstCtrl = null;
        void Upd(PdfPoint pt)
        {
            minx = Math.Min(minx, pt.X); maxx = Math.Max(maxx, pt.X);
            miny = Math.Min(miny, pt.Y); maxy = Math.Max(maxy, pt.Y);
        }
        foreach (var sp in p)
        {
            foreach (var cmd in sp.Commands)
            {
                switch (cmd)
                {
                    case PdfSubpath.Move m: moves++; Upd(m.Location); break;
                    case PdfSubpath.Line l: lines++; Upd(l.From); Upd(l.To); break;
                    case PdfSubpath.CubicBezierCurve b:
                        curves++;
                        Upd(b.FirstControlPoint); Upd(b.SecondControlPoint); Upd(b.EndPoint);
                        firstCtrl ??= new[] { b.FirstControlPoint.X, b.FirstControlPoint.Y };
                        break;
                    case PdfSubpath.QuadraticBezierCurve q:
                        curves++;
                        Upd(q.ControlPoint); Upd(q.EndPoint);
                        break;
                    case PdfSubpath.Close: closes++; break;
                }
            }
        }
        var st = pathIdx < pathStates.Count ? pathStates[pathIdx] : default;
        records.Add(new Dictionary<string, object?>
        {
            ["kind"] = "path",
            ["seq"] = pathIdx,
            ["clipping"] = p.IsClipping,
            ["filled"] = p.IsFilled,
            ["stroked"] = p.IsStroked,
            ["fillingRule"] = p.FillingRule.ToString(),
            ["fill"] = ColorArr(p.FillColor),
            ["stroke"] = ColorArr(p.StrokeColor),
            ["lw_pt"] = p.LineWidth,
            ["bbox_pt"] = minx <= maxx ? new[] { minx, miny, maxx, maxy } : null,
            ["lines"] = lines,
            ["curves"] = curves,
            ["moves"] = moves,
            ["closes"] = closes,
            ["first_bezier_ctrl_pt"] = firstCtrl,
            // 状態追跡による補完値（第3段）
            ["ops_op"] = st.op,
            ["ops_clip"] = st.clip,
            ["lw_dev_pt"] = st.lwDev,
            ["ctm_scale"] = st.scale,
            ["ca"] = st.ca,
            ["CA"] = st.CA,
            ["layer"] = st.layer,
        });
        pathIdx++;
    }

    // ---- 文字（グリフ単位） ----
    foreach (var l in page.Letters)
    {
        // TextSequence は Tj/TJ（テキスト表示オペレータ）単位の 1 始まり連番
        //（実測: distinct 75 = show op 数、欠番なし）
        var ts = l.TextSequence >= 1 && l.TextSequence <= textStates.Count
            ? textStates[l.TextSequence - 1]
            : ((double, double, string?)?)null;
        records.Add(new Dictionary<string, object?>
        {
            ["kind"] = "letter",
            ["value"] = l.Value,
            ["font"] = l.FontName,
            ["size_pt"] = l.PointSize,
            ["color"] = ColorArr(l.Color),
            ["origin_pt"] = new[] { l.StartBaseLine.X, l.StartBaseLine.Y },
            ["text_seq"] = l.TextSequence,
            ["bbox_pt"] = new[]
            {
                l.GlyphRectangle.Left, l.GlyphRectangle.Bottom,
                l.GlyphRectangle.Right, l.GlyphRectangle.Top
            },
            ["ca"] = ts?.Item1,
            ["CA"] = ts?.Item2,
            ["layer"] = ts?.Item3,
        });
    }

    // ---- 画像 ----
    int imgIdx = 0;
    foreach (var im in page.GetImages())
    {
        var ist = imgIdx < imageStates.Count ? imageStates[imgIdx] : default;
        records.Add(new Dictionary<string, object?>
        {
            ["kind"] = "image",
            ["w_px"] = im.WidthInSamples,
            ["h_px"] = im.HeightInSamples,
            ["bbox_pt"] = new[]
            {
                im.Bounds.Left, im.Bounds.Bottom, im.Bounds.Right, im.Bounds.Top
            },
            ["ca"] = ist.ca,
            ["layer"] = ist.layer,
        });
        imgIdx++;
    }

    // ---- レイヤの完成品API（GetMarkedContents / GetOptionalContents）との突合せ用 ----
    try
    {
        var oc = page.GetOptionalContents();
        Console.WriteLine($"GetOptionalContents: {oc.Count}レイヤ [{string.Join(", ", oc.Keys)}]");
    }
    catch (Exception e)
    {
        Console.WriteLine($"GetOptionalContents失敗: {e.Message}");
    }

    int gsCount = page.Operations.Count(o => o.Operator == "gs");
    Console.WriteLine($"paths={page.Paths.Count} letters={page.Letters.Count} " +
                      $"images={records.Count(r => (string?)r["kind"] == "image")} " +
                      $"gs_ops={gsCount} operations={page.Operations.Count}");

    // ---- 注釈（EPDFSHX 検証用） ----
    foreach (var a in page.ExperimentalAccess.GetAnnotations())
    {
        records.Add(new Dictionary<string, object?>
        {
            ["kind"] = "annotation",
            ["type"] = a.Type.ToString(),
            ["content"] = a.Content,
            ["bbox_pt"] = new[]
            {
                a.Rectangle.Left, a.Rectangle.Bottom,
                a.Rectangle.Right, a.Rectangle.Top
            },
        });
    }
}

var json = JsonSerializer.Serialize(records,
    new JsonSerializerOptions { WriteIndented = false });
File.WriteAllText(args[1], json);
Console.WriteLine($"JSON出力: {args[1]} ({records.Count}件)");
return 0;
