// 取得アダプタ（PDF印刷経路）: プロットPDF → SVG IR 写像。
// 写像規則はテストチャートと実物図面（回転文字・空白保持を含む）で実測確定したものを基準とする:
// - パス: ベジェ制御点保持・色は最終RGB・線幅はCTMスケール補正後のmm値・fill-rule
// - テキスト: 表示オペレータ単位のラン。Tz→幅係数、グリフ位置列で字送りを固定、
//   回転はベースライン方向から算出（Y反転に伴い符号反転）
// - 透過: ExtGState ca/CA → fill-opacity / stroke-opacity
// - レイヤ: /OC BDC → 要素単位の data-layer（z順保存と両立させるため g を使わない）
// - クリップ: W/W* + n → clipPath 参照（同一幾何は集約）
// - 画像: JPEG + SMask を合成した PNG を埋め込み
// - 座標: pt → mm 正規化、Y軸反転
// - 線幅0（デバイス最細線）は 0.05mm 固定幅へ置換し診断ログに記録（data-hairline は不採用と決定）
// - 描画順は内容ストリーム順を保存する。パス・文字・画像を型別に別リストで得るが、走査時に
//   採番したグローバル順序で型横断に安定ソートして Nodes へ積むため、種別をまたぐ z順（画像・
//   グラデ画像がパスの前後どちらに来るか等）も保存される（ワイプアウト=白塗りの成立条件を含む）。
//   /Do は XObject の Subtype を判別し、Image を採番、Form は固有リソース・Matrix を反映して
//   内容ストリームを depth-first に再帰走査する。解析不能時は全種別を末尾送り＋既定属性へ倒す。
//
// 状態追跡の前提（実証済み）:
// - 描画オペレータと Page.Paths は 1:1（EndPath はクリップ確定時のみエントリ）
// - Letter.TextSequence はテキスト表示オペレータ単位の 1 始まり連番
// - Form を depth-first 展開した描画操作は Page.Paths / Letters / GetImages の抽出順と 1:1
using System.Globalization;
using System.Text.RegularExpressions;
using CadPeta.Acquire.Common;
using CadPeta.Ir;
using CadPeta.UseCases;
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

namespace CadPeta.Acquire.Pdf;

public sealed class PdfIrAcquirer : IIrAcquirer
{
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

        public IReadOnlyList<IFilter> GetFilters(DictionaryToken dictionary,
                                                 IPdfTokenScanner scanner)
        {
            IToken ResolveOne(IToken token) => token is IndirectReferenceToken reference
                ? scanner.Get(reference.Data)?.Data ?? token : token;
            var resolved = new DictionaryToken(dictionary.Data.ToDictionary(
                pair => NameToken.Create(pair.Key), pair => ResolveOne(pair.Value)));
            return inner.GetFilters(resolved);
        }
    }

    const double Pt2Mm = 25.4 / 72.0; // 1pt = 1/72 inch、1 inch = 25.4mm（PDF の単位系）
    // PDF Tz（水平スケーリング）は % 値。100 が標準幅（PDF 32000-1 9.3.4）
    const double TzPercentBase = 100.0;
    // SVG に PDF の「線幅0=デバイス最細線」の概念がないため、最細線は固定幅へ置換する
    // （非可逆の正規化として診断ログに記録する。値はコンテンツ量子 0.03pt の約5倍）
    const double HairlineMm = 0.05;

    readonly TextWriter? _log;
    readonly TextWriter? _verbose;

    /// <param name="log">情報メッセージの出力先（null で沈黙）。診断ログとは別の進行報告。</param>
    public PdfIrAcquirer(TextWriter? log = null, TextWriter? verbose = null)
    { _log = log; _verbose = verbose ?? log; }

    public IrDocument Acquire(string sourcePath, IrDiagnostics diag)
    {
        var ir = new IrDocument();

        using (var doc = PdfDocument.Open(sourcePath))
        {
            var page = doc.GetPage(1); // プロットPDFは1ページ前提（PDF印刷経路の入力形式。ページ番号は1始まり）
            ir.WidthMm = page.Width * Pt2Mm;
            ir.HeightMm = page.Height * Pt2Mm;
            _verbose?.WriteLine($"page: {page.Width} x {page.Height} pt = " +
                            $"{ir.WidthMm:F3} x {ir.HeightMm:F3} mm");

            // 座標変換: pt（PDF、Y上向き）→ mm（IR、Y下向き）
            double Xmm(double xPt) => xPt * Pt2Mm;
            double Ymm(double yPt) => ir.HeightMm - yPt * Pt2Mm;

            // ---------- 間接参照の解決子とページリソース ----------
            IToken Resolve(IToken t) =>
                t is IndirectReferenceToken r ? doc.Structure.GetObject(r.Data).Data : t;

            DictionaryToken? DictOf(IToken? t) =>
                t == null ? null : Resolve(t) as DictionaryToken;

            // /Resources はページ属性の継承対象で、ページ辞書に無ければ親（Pages ノード）を
            // 上へ辿って探す。AutoCAD の PDF はページ辞書に直接持つが、一部の CAD
            // （ODA 系 PDF エクスポータ）は Pages ノード側に置くため、辿らないと
            // 透過（ExtGState）・レイヤ（Properties）を取り落とす。無限ループを避けるため
            // 辿る深さに上限を設ける（ページツリーの実用的な深さを超える値）
            const int MaxParentDepth = 64;
            DictionaryToken? resources = null;
            var resNode = page.Dictionary;
            for (int depth = 0; resNode != null && depth < MaxParentDepth; depth++)
            {
                if (resNode.TryGet(NameToken.Create("Resources"), out var resTok))
                {
                    resources = DictOf(resTok);
                    break;
                }
                resNode = resNode.TryGet(NameToken.Create("Parent"), out var parentTok)
                    ? DictOf(parentTok) : null;
            }
            double GsNum(DictionaryToken d, string key, double fallback)
                => d.TryGet(NameToken.Create(key), out var v) && Resolve(v) is NumericToken n
                    ? (double)n.Data : fallback;

            // /Do で呼ばれる XObject の /Subtype を返す。XObject は通常ストリームのため、
            // 間接参照を解決した StreamToken の辞書から読む（DictOf は DictionaryToken 専用で流用不可）。
            // 名前・ストリーム・Subtype のいずれかを解決できなければ false（呼び出し側が確定不能として扱う）。
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

            // ---------- 状態機械（CTM・線幅・透過・Tz・レイヤ・クリップの追跡） ----------
            var ctm = new double[] { 1, 0, 0, 1, 0, 0 };  // 単位行列
            // PDF グラフィックス状態の既定値: 線幅1（ユーザー空間単位）・完全不透明・Tz標準
            double lw = 1.0, ca = 1.0, CA = 1.0, tz = TzPercentBase;
            string? clip = null;
            var stack = new Stack<(double[] ctm, double lw, double ca, double CA, double tz, string? clip)>();
            var layerStack = new Stack<string?>();
            bool clipPending = false, clipEvenOdd = false;
            int clipCounter = 0;
            // 描画順（内容ストリーム順）の通し番号。パス塗り・テキスト表示・画像の各状態を積む点で
            // 採番し、後段で型横断の安定ソートキーに使う（3バッチ出力でも真の z順へ復元するため）。
            int drawOrder = 0;
            // XObject の Subtype を解決できない /Do を1件でも見たか（画像対応を確定不能へ倒す）。
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
                _log?.WriteLine(
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

            // 画像は一度だけ実体化して共有する（二重列挙回避）。imageStates（最上位 /Do・インライン
            // 画像を内容ストリーム順で採番したもの）と件数が一致し、かつ未解決 XObject が無いときだけ、
            // 添字対応で実 z順・属性を使える。件数不一致（Form 内包画像・PS 等）や未解決があるときは
            // 添字対応を一切信用せず、全画像を末尾送り＋既定属性へ倒す（下の画像ループ参照）。
            var images = page.GetImages().ToList();
            bool imagesAligned = drawingAligned && imageStates.Count == images.Count;
            if (!imagesAligned)
                diag.Add("プロファイル外: 画像の操作・抽出対応を確定不能（件数不一致・XObject解決失敗・Form入れ子等）。" +
                         "z順・レイヤ/クリップ/透過の対応を確定できず末尾へ近似配置",
                         $"imageStates={imageStates.Count} GetImages={images.Count} " +
                         $"unresolvedXObject={hasUnresolvedXObject}");

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
                                // 直前終点との不連続判定（pt。コンテンツ量子 0.03pt より十分小さい閾値）
                                const double JoinTolPt = 1e-6;
                                if (cur == null || Math.Abs(cur.Value.X - l.From.X) > JoinTolPt
                                                || Math.Abs(cur.Value.Y - l.From.Y) > JoinTolPt)
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
                // string.Create(IFormatProvider,…) は net48 に無いため等価の Invariant で書く
                FormattableString.Invariant(
                    $"bbox_mm=({b[0]:F1},{b[1]:F1})-({b[2]:F1},{b[3]:F1})");

            static IrRgb ToIr(IColor c)
            {
                var (r, g, b) = c.ToRGBValues();
                return new IrRgb((double)r, (double)g, (double)b);
            }

            // 型別3ループが生む全ノードを (描画順, ノード) で集め、最後に安定ソートして ir.Nodes へ
            // 積む。これで「全パス→全文字→全画像」の型別バッチではなく、真の内容ストリーム順（z順）
            // が復元される。範囲外の状態は order に int.MaxValue（末尾送り）を使い、default タプルの
            // order==0 を使ってはならない（0 は先頭へ誤ソートされる）。
            var tagged = new List<(int order, IrNode node)>();

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
                        double lwMm = st.lwDevPt * Pt2Mm;
                        if (lwMm <= 0)
                        {
                            diag.Add($"最細線の固定幅置換（線幅0→{HairlineMm}mm）",
                                     $"path seq={pathIdx} {BboxStr(bbox)}");
                            lwMm = HairlineMm;
                        }
                        node.StrokeWidthMm = lwMm;
                    }
                    // 範囲外（page.Paths が pathStates より多い）は末尾送り。IndexOutOfRange を避け、
                    // 既存の件数不一致警告（上）で顕在化させる。
                    int order = drawingAligned && pathIdx < pathStates.Count
                        ? pathStates[pathIdx].order : int.MaxValue;
                    tagged.Add((order, node));
                }
                pathIdx++;
            }

            // ---------- 文字 → IrText（テキスト表示オペレータ単位のラン） ----------
            // PDF の BaseFont は PostScript 名で、ファミリ名と一致しない書体が多い。名前が同じなら
            // 同じ書体だと想定して生名のまま流すと、GDI が失敗せず既定書体へ黙って差し替えるため、
            // 字形も字送りも変わったまま出力される（実測: TimesNewRomanPSMT・CourierNewPSMT・
            // YuGothic-Regular はいずれも Microsoft Sans Serif に化ける）。GDI に解決させて
            // 読み戻しで検証する（GdiFontMetrics.FamilyNameForPostScriptName）。
            var unknownFonts = new HashSet<string>();
            string MapFont(string raw)
            {
                var name = Regex.Replace(raw, @"^[A-Z]{6}\+", "");  // サブセット接頭辞を除去
                string? family = GdiFontMetrics.FamilyNameForPostScriptName(name);
                if (family != null) return family;
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
                    // 範囲外は末尾送り（order=int.MaxValue）。default タプルの order==0 で先頭へ
                    // 誤ソートしないよう、明示的に番兵を入れた名前付きタプルを使う。
                    : (ca: 1.0, layer: (string?)null, clip: (string?)null, tz: TzPercentBase,
                       order: int.MaxValue);
                if (first.Color == null)
                    diag.Add("既定色の補完（色未設定→黒）", $"text seq={run.Key} '{first.Value}'");

                // 回転文字（実物図面の縦寸法値で実測した類型）: ベースライン方向から回転角を得る。
                // PDF（Y上向き）の角度を IR（Y下向き、時計回り正）の rotate へ符号反転で写す
                var lastL = letters[letters.Count - 1]; // ^1 は net48（System.Index 不在）で使えない
                double bx = lastL.EndBaseLine.X - first.StartBaseLine.X;
                double by = lastL.EndBaseLine.Y - first.StartBaseLine.Y;
                // ベースライン長² がこの値以下（単一グリフ等で方向が定義できない）なら水平とみなす
                const double MinBaselineSqPt = 1e-12;
                // 算出角がこの値以下なら数値誤差由来とみなし水平のまま扱う（度）
                const double RotationEpsDeg = 0.01;
                double rotDeg = 0.0;
                if (bx * bx + by * by > MinBaselineSqPt)
                {
                    double a = Math.Atan2(by, bx) * 180.0 / Math.PI;
                    if (Math.Abs(a) > RotationEpsDeg) { rotDeg = -a; rotatedRuns++; }
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
                               + (l.StartBaseLine.Y - first.StartBaseLine.Y) * uy) * Pt2Mm).ToArray();
                    }
                }

                tagged.Add((ts.order, new IrText
                {
                    Value = string.Concat(letters.Select(l => l.Value)),
                    XMm = Xmm(first.StartBaseLine.X),
                    YMm = Ymm(first.StartBaseLine.Y),
                    GlyphXsMm = glyphXs,
                    FontFamily = MapFont(first.FontName ?? ""),
                    FontSizeMm = first.PointSize * Pt2Mm,
                    WidthFactor = ts.tz / TzPercentBase,
                    RotationDeg = rotDeg,
                    Fill = first.Color != null ? ToIr(first.Color) : new IrRgb(0, 0, 0),
                    FillOpacity = ts.ca,
                    DataLayer = ts.layer,
                    ClipId = ts.clip,
                }));
            }
            if (rotatedRuns > 0)
                _verbose?.WriteLine(MessageCatalogs.Current.Format("acquire.pdf_rotated_runs", rotatedRuns));
            if (unknownFonts.Count > 0)
            {
                // 書体の取り違えは字形と字送りを変える非可逆な劣化なので、進行報告だけで済ませず
                // 診断へ残す（他の近似処理と同列に扱う）
                _log?.WriteLine(MessageCatalogs.Current.Format("acquire.pdf_unknown_fonts",
                    string.Join(", ", unknownFonts)));
                diag.Add("フォント名を解決できず（生名のまま出力。字形・字送りが変わり得る）",
                    $"{unknownFonts.Count}件: {string.Join(", ", unknownFonts)}");
            }

            // ---------- 画像 → IrImage（JPEG + SMask → PNG 合成） ----------
            // /Rotate ページでは PdfPig が画像境界を表示座標系の回転矩形（四隅＋Rotation）で
            // 返す（kida 実測: /Rotate 270 → Rotation=90、Left/Top は軸平行を前提にすると
            // 高さが負になる。2026-07-07）。四半回転は画素へ無劣化で焼き込み軸平行配置に
            // 直す。回転 0 は従来経路のまま（既存コーパスとのバイト一致を保つ）
            int imgIdx = 0, rotatedImages = 0;
            // 画像境界の回転角がこの値以下なら数値誤差由来とみなし無回転と扱う（度）
            const double ImageRotationEpsDeg = 0.01;
            foreach (var im in images)
            {
                // 添字対応が信用できる（件数一致かつ未解決 XObject 無し）ときだけ実 z順・属性を使う。
                // それ以外は order=int.MaxValue（末尾送り）＋既定属性へ倒す（上のガード診断で顕在化）。
                bool imgHasState = imagesAligned && imgIdx < imageStates.Count;
                var ist = imgHasState ? imageStates[imgIdx] : default;
                int order = imgHasState ? imageStates[imgIdx].order : int.MaxValue;
                var bounds = im.BoundingBox;
                int quarterCcw = (((int)Math.Round(bounds.Rotation / 90.0)) % 4 + 4) % 4;
                bool isQuarter = Math.Abs(bounds.Rotation - Math.Round(bounds.Rotation / 90.0) * 90.0)
                                 <= ImageRotationEpsDeg;
                double[] bboxMm;
                double xMm, yMm, wMm, hMm;
                if (isQuarter && quarterCcw == 0)
                {
                    bboxMm = new[] { Xmm(bounds.Left), Ymm(bounds.Top), Xmm(bounds.Right), Ymm(bounds.Bottom) };
                    xMm = Xmm(bounds.Left);
                    yMm = Ymm(bounds.Top);
                    wMm = (bounds.Right - bounds.Left) * Pt2Mm;
                    hMm = (bounds.Top - bounds.Bottom) * Pt2Mm;
                }
                else
                {
                    // 回転矩形は四隅の min/max で軸平行 bbox に直す
                    var cs = new[] { bounds.BottomLeft, bounds.BottomRight, bounds.TopLeft, bounds.TopRight };
                    double minX = cs.Min(c => c.X), maxX = cs.Max(c => c.X);
                    double minY = cs.Min(c => c.Y), maxY = cs.Max(c => c.Y);
                    bboxMm = new[] { Xmm(minX), Ymm(maxY), Xmm(maxX), Ymm(minY) };
                    xMm = Xmm(minX);
                    yMm = Ymm(maxY);
                    wMm = (maxX - minX) * Pt2Mm;
                    hMm = (maxY - minY) * Pt2Mm;
                    if (isQuarter) rotatedImages++;
                    else diag.Add("プロファイル外: 非直角回転の画像（無回転で軸平行bboxへ近似配置）",
                                  $"image seq={imgIdx} rot={bounds.Rotation:F1}° {BboxStr(bboxMm)}");
                }
                byte[] png = PdfImageDecoder.ComposePng(im, doc, out bool hadSMask,
                                                        isQuarter ? quarterCcw : 0);
                tagged.Add((order, new IrImage
                {
                    XMm = xMm,
                    YMm = yMm,
                    WidthMm = wMm,
                    HeightMm = hMm,
                    PngBytes = png,
                    DataLayer = ist.layer,
                    ClipId = ist.clip,
                }));
                if (hadSMask)
                    diag.Add("SOLID×透過の画像化を継承（SMask合成PNGの埋め込み、非可逆JPEG由来）",
                             $"image seq={imgIdx} {im.WidthInSamples}x{im.HeightInSamples}px {BboxStr(bboxMm)}");
                // ca 未表現の診断は添字対応が信用できるときだけ出す（不整合時は ist=default で ca=0 に
                // なり偽の透過警告を撒くため）。不整合自体は上のガード診断で既に記録済み。
                if (imgHasState && ist.ca < 1.0)
                    diag.Add("プロファイル外: 画像への ca 透過（未表現）", $"image seq={imgIdx} ca={ist.ca}");
                imgIdx++;
            }

            // 3ループが集めた全ノードを描画順で安定ソートして ir.Nodes へ確定する（LINQ OrderBy は安定）。
            foreach (var (_, node) in tagged.OrderBy(t => t.order))
                ir.Nodes.Add(node);
            if (rotatedImages > 0)
                _verbose?.WriteLine(MessageCatalogs.Current.Format("acquire.pdf_rotated_images", rotatedImages));

            // ---------- SHX 注釈（EPDFSHX、あれば補助情報として記録） ----------
            int annots = 0;
            foreach (var a in page.GetAnnotations())
            {
                annots++;
                diag.Add("SHX輪郭化の元文字列注釈（回収のみ・IR未反映）",
                         $"'{a.Content}' rect_pt=({a.Rectangle.Left:F1},{a.Rectangle.Bottom:F1})");
            }

            _verbose?.WriteLine($"IR: paths={ir.Nodes.OfType<IrPath>().Count()} " +
                            $"texts={ir.Nodes.OfType<IrText>().Count()} " +
                            $"images={ir.Nodes.OfType<IrImage>().Count()} " +
                            $"clips={ir.ClipDefs.Count} annots={annots}");
        }

        DedupeClips(ir);
        return ir;
    }

    /// <summary>クリップ定義の同一幾何の集約（旧 id は代表 id へ付け替え）。</summary>
    void DedupeClips(IrDocument ir)
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
        _verbose?.WriteLine(MessageCatalogs.Current.Format("acquire.pdf_clip_merged", remap.Count, kept.Count));
    }
}
