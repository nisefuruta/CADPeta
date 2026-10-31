// 取得アダプタ（構造SVG書き出し: DB 直接取得）の4製品共有本体。
// モデル空間の選択オブジェクトを図面データベースから直接読み、SVG IR へ写像する。
// 4製品の取得器は SDK 名前空間・製品名文字列・LWDEFAULT を読む Application の所在以外は
// 同文だったため、CadDataExportCommands.cs と同じ方式（#if 分岐＋using エイリアス＋
// csproj の Compile Include リンク）でここへ1本化する。正立フラグ API の製品差は
// 各製品の LinetypeShapeUprightCompat.cs が吸収する。
//
// - 座標系: 図面単位→mm（尺度 1:N で k=1/N 倍）、Y軸反転（CAD 上向き正→IR 下向き正）は
//   本アダプタの責務。回転角も同時に符号反転する
// - ByLayer/ByBlock の色・透過・線種・線の太さは取得時に実効値へ解決し、解決前の指定方法を
//   data-color-method に記録する。ACI/TrueColor の区別は Color オブジェクトの種別判定で行い
//   推測を含まない
// - ブロック参照は変換行列付きグループ（IrGroup）に展開する。行列は文書写像 D（k倍＋Y反転）で
//   共役変換し、子ノードは常に「ローカル座標を D で写した値」を持つ（入れ子も同じ規則）
// - 線の太さ（lineweight）は用紙上の絶対幅のためグループ拡大率で割って格納する
//   （SVG のグループ変換で拡大されて元の絶対幅に戻る）。ポリライン幅は図面単位の幾何のため
//   k 倍だけ行い、グループ変換に任せる。破線パターン（LTSCALE・線種尺度が生む長さ）と
//   複雑線種の記号（寸法・オフセット・出現距離）は線の太さと同じく用紙上の絶対値のため、
//   曲線長との比較・格納の直前でグループ拡大率で割る（ブロック挿入尺度2で記号が実測4倍近く
//   肥大・個数が半減する欠陥の是正、2026-07-10）
// - 未対応エンティティは分解（Explode）で構成要素へ展開して写像し、分解できない類型は
//   件数と対象を診断ログへ記録してスキップする（1個の特殊オブジェクトで全体を失敗させない）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

#if CADPETA_ACAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SdkApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using SdkException = Autodesk.AutoCAD.Runtime.Exception;
using SdkErrorStatus = Autodesk.AutoCAD.Runtime.ErrorStatus;
using SdkPolyline = Autodesk.AutoCAD.DatabaseServices.Polyline;
namespace CadPeta.Acquire.Acad;
#elif CADPETA_ARES
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using SdkApplication = Teigha.ApplicationServices.Application;
using SdkException = Teigha.Runtime.Exception;
using SdkErrorStatus = Teigha.Runtime.ErrorStatus;
using SdkPolyline = Teigha.DatabaseServices.Polyline;
namespace CadPeta.Acquire.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.Colors;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.Geometry;
using SdkApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
using SdkException = Itjp.Ijcad.Runtime.Exception;
using SdkErrorStatus = Itjp.Ijcad.Runtime.ErrorStatus;
using SdkPolyline = Itjp.Ijcad.DatabaseServices.Polyline;
namespace CadPeta.Acquire.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
// エイリアスをコンパイル単位スコープ（namespace 宣言の前）に置くことでグローバル解決を
// 強制する: 名前空間 CadPeta.Acquire.Bricscad の本体で Bricscad.ApplicationServices... と
// 書くと自分自身の名前空間が「Bricscad」を隠す（C# の既知の名前空間シャドーイング）
using SdkApplication = Bricscad.ApplicationServices.Application;
using SdkException = Teigha.Runtime.Exception;
using SdkErrorStatus = Teigha.Runtime.ErrorStatus;
using SdkPolyline = Teigha.DatabaseServices.Polyline;
namespace CadPeta.Acquire.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Acquire.Common;
using CadPeta.Acquire.Raster;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using CadPeta.UseCases;

public sealed class CadIrAcquirer
{
#if CADPETA_ACAD
    const string ProductName = "AutoCAD";
#elif CADPETA_ARES
    const string ProductName = "ARES";
#elif CADPETA_IJCAD
    const string ProductName = "IJCAD";
#else
    const string ProductName = "BricsCAD";
#endif

    readonly TextWriter? _log;
    readonly TextWriter? _verbose;

    /// <param name="log">情報メッセージの出力先（null で沈黙）。診断ログとは別の進行報告。</param>
    public CadIrAcquirer(TextWriter? log = null, TextWriter? verbose = null)
    {
        _log = log;
        _verbose = verbose ?? log;
    }

    /// <summary>分解（Explode）の再帰上限。複合エンティティの入れ子はこの深さで打ち切り、
    /// 打ち切りはスキップとして診断ログへ記録する。</summary>
    const int MaxExplodeDepth = 8;

    /// <summary>
    /// 選択オブジェクト列を IR 文書へ写像する。scaleDenominator は貼付尺度 1:N の N
    /// （1図面単位=1mm を 1:1 とする）。文書範囲は選択の外接矩形＋marginMm。
    /// shxSubstitute=true は SHX 文字を輪郭化せず代替書体の text で出力する（既定は輪郭化）。
    /// 1つも写像できなければ Nodes が空の文書を返す（呼び出し側が中止を判断する）。
    /// </summary>
    public IrDocument Acquire(CadTransaction tr, Database db, IReadOnlyList<ObjectId> ids,
                              double scaleDenominator, double marginMm, IrDiagnostics diag,
                              bool shxSubstitute = false,
                              double gradientDpi = AcquireDefaults.GradientDefaultDpi)
    {
        if (scaleDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(scaleDenominator),
                $"尺度 1:N の N は正の値を指定する: {scaleDenominator}");
        if (!CadPeta.Geometry.Numbers.IsPositiveFinite(gradientDpi))
            throw new ArgumentOutOfRangeException(nameof(gradientDpi), "グラデーションDPIは正の有限値が必要");

        double k = 1.0 / scaleDenominator;
        var ctx = new Ctx
        {
            Tr = tr,
            Db = db,
            Diag = diag,
            Map = (x, y) => (x * k, -y * k),
            GeomScale = k,
            GradientDpi = gradientDpi,
            GradientBudget = new GradientRasterBudget(AcquireDefaults.GradientMaxPixelsPerAcquire),
            DocumentFromLocal = IrMatrix.Identity,
            TextCtx = new CadTextMapper.TextMapContext { ShxSubstitute = shxSubstitute, Db = db },
        };
        var ir = new IrDocument();
        int sourceCount = 0;
        foreach (var id in SortByDrawOrder(tr, ids))
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent)
            {
                diag.Add("構造SVG書き出し: 非図形オブジェクトのスキップ", id.ObjectClass?.DxfName ?? "(不明)");
                continue;
            }
            sourceCount++;
            MapEntity(ent, ctx, ir.Nodes, depth: 0);
        }

        // 文書範囲は写像済みノードの保守的 bbox（線幅の半分・文字クアッド込み）の合併で取る。
        // EMF 出力の事前クリップも同じ物差し（IrBounds）で交差判定するため、境界上の図形が
        // 切断・輪郭化されない（外接矩形を物差しにすると、境界の文字が保守的クアッドの
        // はみ出し分だけ「交差」と誤判定され輪郭化された。実測）。
        // 保守的な分だけ範囲は広めに出るが、切れるより安全側である
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var node in ir.Nodes)
        {
            var b = Geometry.IrBounds.NodeBbox(node);
            if (b[0] > b[2])
                continue; // 空グループ等の縮退 bbox
            minX = Math.Min(minX, b[0]); maxX = Math.Max(maxX, b[2]);
            minY = Math.Min(minY, b[1]); maxY = Math.Max(maxY, b[3]);
        }

        if (ir.Nodes.Count == 0 || minX > maxX)
        {
            _log?.WriteLine(MessageCatalogs.Current.Format("acquire.data_empty", sourceCount));
            ir.Nodes.Clear();
            return ir;
        }

        ir.ViewMinXMm = minX - marginMm;
        ir.ViewMinYMm = minY - marginMm;
        ir.WidthMm = (maxX - minX) + 2 * marginMm;
        ir.HeightMm = (maxY - minY) + 2 * marginMm;
        _verbose?.WriteLine(MessageCatalogs.Current.Format("acquire.data_result",
            sourceCount, ir.Nodes.Count, ir.WidthMm, ir.HeightMm, scaleDenominator, marginMm));
        return ir;
    }

    /// <summary>
    /// 選択オブジェクトを実描画順（DRAWORDER 反映済みの z順）へ並べ替える。
    /// 選択セットの列挙順は新しい順で描画順を保存しないため、そのまま写像すると
    /// ワイプアウト（白塗りの上下関係）が崩れる（TESTCHART2 実測）。
    /// </summary>
    static List<ObjectId> SortByDrawOrder(CadTransaction tr, IReadOnlyList<ObjectId> ids)
    {
        var selected = new HashSet<ObjectId>(ids);
        var result = new List<ObjectId>(ids.Count);
        var visitedOwners = new HashSet<ObjectId>();
        foreach (var id in ids)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent
                || !visitedOwners.Add(ent.OwnerId))
                continue;
            if (tr.GetObject(ent.OwnerId, OpenMode.ForRead) is not BlockTableRecord btr)
                continue;
            // DRAWORDER の全順序（無ければブロックの格納順=作成順が描画順）
            if (!btr.DrawOrderTableId.IsNull
                && tr.GetObject(btr.DrawOrderTableId, OpenMode.ForRead) is DrawOrderTable dot)
            {
                foreach (ObjectId oid in dot.GetFullDrawOrder(0))
                    if (selected.Contains(oid))
                        result.Add(oid);
            }
            else
            {
                foreach (ObjectId oid in btr)
                    if (selected.Contains(oid))
                        result.Add(oid);
            }
        }
        // 拾えなかった残り（非図形等）は入力順のまま末尾へ（写像側でスキップ判定される）
        var placed = new HashSet<ObjectId>(result);
        foreach (var id in ids)
            if (!placed.Contains(id))
                result.Add(id);
        return result;
    }

    // ---------- 走査文脈 ----------

    /// <summary>ByBlock の実効値（ブロック参照で解決済みの挿入側の値）。</summary>
    sealed record ByBlockStyle(IrRgb Rgb, string ColorMethod, int? Aci, double Opacity,
                               double[]? DashDu, double LineweightMm);

    sealed class Ctx
    {
        public CadTransaction Tr = null!;
        public Database Db = null!;
        public IrDiagnostics Diag = null!;
        public PointMap Map = null!;
        public double GeomScale;
        public double GradientDpi;
        public GradientRasterBudget GradientBudget = null!;
        public IrMatrix DocumentFromLocal = IrMatrix.Identity;
        /// <summary>文字写像の設定と取得1回のあいだ共有する状態（SHX パス解決キャッシュ等）。</summary>
        public CadTextMapper.TextMapContext TextCtx = null!;
        /// <summary>ここまでのグループ変換の等方スケール積。線の太さ（絶対幅）の格納値の補正に使う。</summary>
        public double GroupScale = 1.0;
        /// <summary>ブロック文脈の実効レイヤ。レイヤ「0」の子はこのレイヤを継承する。</summary>
        public string? BlockLayer;
        /// <summary>ByBlock 指定の解決先。トップレベル（ブロック外）は null。</summary>
        public ByBlockStyle? ByBlock;

        public Ctx CloneForBlock(IrMatrix groupTransform, string blockLayer, ByBlockStyle byBlock) => new()
        {
            Tr = Tr,
            Db = Db,
            Diag = Diag,
            Map = Map,
            GeomScale = GeomScale,
            GradientDpi = GradientDpi,
            GradientBudget = GradientBudget,
            DocumentFromLocal = DocumentFromLocal.Multiply(groupTransform),
            TextCtx = TextCtx,
            GroupScale = GroupScale * groupTransform.ScaleFactor,
            BlockLayer = blockLayer,
            ByBlock = byBlock,
        };
    }

    // ---------- エンティティ写像 ----------

    void MapEntity(Entity ent, Ctx ctx, List<IrNode> sink, int depth)
    {
        if (!ent.Visible)
            return;
        if (ctx.Tr.GetObject(ent.LayerId, OpenMode.ForRead) is LayerTableRecord ltr)
        {
            // 非表示レイヤ（オフ・フリーズ）の図形は画面と同じく描画しない（ブロックの子で起こる）
            if (ltr.IsOff || ltr.IsFrozen)
                return;
            // 非印刷レイヤ（Defpoints・寸法の定義点等）はプロットと同じく描画しない。
            // 明示選択で消えると意図が伝わらないため記録する
            if (!ltr.IsPlottable)
            {
                ctx.Diag.Add("構造SVG書き出し: 非印刷レイヤの図形のスキップ", $"{DxfName(ent)} layer={ent.Layer}");
                return;
            }
        }

        switch (ent)
        {
            case Line line:
            {
                var p = NewPath(ent, ctx, stroke: true);
                var a = ctx.Map(line.StartPoint.X, line.StartPoint.Y);
                var b = ctx.Map(line.EndPoint.X, line.EndPoint.Y);
                p.Segments.Add(new IrMove(a.X, a.Y));
                p.Segments.Add(new IrLine(b.X, b.Y));
                sink.Add(p);
                EmitLinetypeSymbols(line, line, ctx, sink);
                break;
            }
            case SdkPolyline pl:
                MapLwPolyline(pl, ctx, sink);
                break;
            case Circle c:
            {
                var p = NewPath(ent, ctx, stroke: true);
                var (ux, uy, vx, vy) = PlaneBasis(c.Normal, c.Radius);
                CurveApprox.EllipticArc(p.Segments, ctx.Map, c.Center.X, c.Center.Y,
                                        ux, uy, vx, vy, 0, 2 * Math.PI, emitMove: true);
                p.Segments.Add(new IrClose());
                sink.Add(p);
                EmitLinetypeSymbols(c, c, ctx, sink);
                break;
            }
            case Arc arc:
            {
                var p = NewPath(ent, ctx, stroke: true);
                var (ux, uy, vx, vy) = PlaneBasis(arc.Normal, arc.Radius);
                CurveApprox.EllipticArc(p.Segments, ctx.Map, arc.Center.X, arc.Center.Y,
                                        ux, uy, vx, vy, arc.StartAngle, arc.EndAngle, emitMove: true);
                sink.Add(p);
                EmitLinetypeSymbols(arc, arc, ctx, sink);
                break;
            }
            case Ellipse el:
            {
                var p = NewPath(ent, ctx, stroke: true);
                // 楕円は P(t) = C + 長軸·cos t + 短軸·sin t。XY へ投影しても楕円のまま厳密に写る
                CurveApprox.EllipticArc(p.Segments, ctx.Map, el.Center.X, el.Center.Y,
                                        el.MajorAxis.X, el.MajorAxis.Y,
                                        el.MinorAxis.X, el.MinorAxis.Y,
                                        el.StartParam, el.EndParam, emitMove: true);
                if (Math.Abs((el.EndParam - el.StartParam) - 2 * Math.PI) < 1e-12)
                    p.Segments.Add(new IrClose());
                sink.Add(p);
                EmitLinetypeSymbols(el, el, ctx, sink);
                break;
            }
            case Spline sp:
                MapSpline(sp, ctx, sink);
                break;
            case DBText text:
                CadTextMapper.MapDbText(text, ctx.Tr, ctx.Map, ctx.GeomScale,
                                         ResolveStyleFor(text, ctx), EffLayer(text, ctx),
                                         DxfName(text), ctx.GroupScale, ctx.TextCtx,
                                         ctx.Diag, sink);
                break;
            case MText mtext:
                CadTextMapper.MapMText(mtext, ctx.Tr, ctx.Map, ctx.GeomScale,
                                        ResolveStyleFor(mtext, ctx), EffLayer(mtext, ctx),
                                        ctx.GroupScale, ctx.TextCtx,
                                        ctx.Diag, sink, ResolveEntityColor);
                break;
            case Hatch hatch:
                MapHatch(hatch, ctx, sink, depth);
                break;
            case Wipeout wipeout:
                MapWipeout(wipeout, ctx, sink);
                break;
            case Solid solid:
            {
                // 2D SOLID（寸法の矢印等）。頂点順 0,1,3,2 が外周（AutoCAD の頂点規約）
                var p = NewPath(ent, ctx, stroke: false);
                p.Fill = ResolveStyleFor(ent, ctx).Rgb;
                p.FillOpacity = ResolveStyleFor(ent, ctx).Opacity;
                var order = new[] { 0, 1, 3, 2 };
                for (int i = 0; i < order.Length; i++)
                {
                    var pt = solid.GetPointAt((short)order[i]);
                    var (x, y) = ctx.Map(pt.X, pt.Y);
                    p.Segments.Add(i == 0 ? new IrMove(x, y) : (IrSegment)new IrLine(x, y));
                }
                p.Segments.Add(new IrClose());
                sink.Add(p);
                break;
            }
            case BlockReference br:
                MapBlockReference(br, ctx, sink, depth);
                break;
            case Dimension dim:
                MapDimension(dim, ctx, sink, depth);
                break;
            default:
                MapByExplode(ent, ctx, sink, depth);
                break;
        }
    }

    void MapLwPolyline(SdkPolyline pl, Ctx ctx, List<IrNode> sink)
    {
        int n = pl.NumberOfVertices;
        if (n < 2)
            return;

        var st = ResolveStyleFor(pl, ctx);
        double cwDu = ResolvePolylineWidthDu(pl, n, ctx);
        int segCount = pl.Closed ? n : n - 1;

        // Plinegen オフ（既定）＋破線パターンありは、頂点区間ごとに独立した開いた曲線として
        // 端部整列する（AutoCAD の実描画と同じ。単一パス＋単一 dasharray では
        // セグメントごとに異なる整列結果を表現できないため、セグメントごとに別の path へ分ける。
        // z順は変わらず、記号展開〔EmitLinetypeSymbols〕も同じセグメント単位のため整合する）。
        // Plinegen オン、または破線を持たない（実線）場合は従来どおり単一パスにまとめる
        // （Plinegen オンは AutoCAD 自身が全長を一体整列するため、単一パスのまま
        // JustifyDashDu が曲線全長で整列する＝NewPath の既定経路で足りる）。
        bool splitPerSegment = !pl.Plinegen && st.DashDu is { Length: > 0 };

        if (!splitPerSegment)
        {
            var p = NewPath(pl, ctx, stroke: true);
            if (cwDu > 0)
                p.StrokeWidthMm = cwDu * ctx.GeomScale;

            var p0 = pl.GetPoint3dAt(0);
            var s0 = ctx.Map(p0.X, p0.Y);
            p.Segments.Add(new IrMove(s0.X, s0.Y));
            for (int i = 0; i < segCount; i++)
            {
                var a = pl.GetPoint3dAt(i);
                var b = pl.GetPoint3dAt((i + 1) % n);
                double bulge = pl.GetBulgeAt(i);
                if (bulge == 0)
                {
                    var q = ctx.Map(b.X, b.Y);
                    p.Segments.Add(new IrLine(q.X, q.Y));
                }
                else
                {
                    CurveApprox.BulgeArc(p.Segments, ctx.Map, a.X, a.Y, b.X, b.Y, bulge);
                }
            }
            if (pl.Closed)
                p.Segments.Add(new IrClose());
            sink.Add(p);
        }
        else
        {
            double strokeWidthMm = cwDu > 0 ? cwDu * ctx.GeomScale : st.LineweightMm / ctx.GroupScale;
            // 破線パターンは用紙上の絶対長のため、セグメントのローカル長と比較する前に
            // グループ拡大率で割って単位を揃える（JustifyDashDu と同じ規則）
            var localDash = st.DashDu!.Select(d => d / ctx.GroupScale).ToArray();
            for (int i = 0; i < segCount; i++)
            {
                var a = pl.GetPoint3dAt(i);
                var b = pl.GetPoint3dAt((i + 1) % n);
                double bulge = pl.GetBulgeAt(i);
                double segLenDu = pl.GetDistanceAtParameter(i + 1) - pl.GetDistanceAtParameter(i);

                var seg = new IrPath
                {
                    DataLayer = EffLayer(pl, ctx),
                    DataEntity = DxfName(pl),
                    DataColorMethod = st.ColorMethod,
                    DataAci = st.Aci,
                    Stroke = st.Rgb,
                    StrokeOpacity = st.Opacity,
                    StrokeWidthMm = strokeWidthMm,
                };
                var justified = LinetypeJustifyMath.JustifyOpen(localDash, segLenDu);
                seg.StrokeDashMm = justified?.Select(v => Math.Abs(v) * ctx.GeomScale).ToArray();

                var sa = ctx.Map(a.X, a.Y);
                seg.Segments.Add(new IrMove(sa.X, sa.Y));
                if (bulge == 0)
                {
                    var q = ctx.Map(b.X, b.Y);
                    seg.Segments.Add(new IrLine(q.X, q.Y));
                }
                else
                {
                    CurveApprox.BulgeArc(seg.Segments, ctx.Map, a.X, a.Y, b.X, b.Y, bulge);
                }
                sink.Add(seg);
            }
        }
        EmitLinetypeSymbols(pl, pl, ctx, sink);
    }

    /// <summary>ポリライン幅（図面単位）を解決する。可変幅は中心線＋平均幅で近似し、
    /// 近似した旨を診断ログへ記録する。幅なし（線の太さ任せ）は0を返す。</summary>
    static double ResolvePolylineWidthDu(SdkPolyline pl, int n, Ctx ctx)
    {
        double cw = pl.ConstantWidth;
        if (cw != 0)
            return cw;
        double sum = 0;
        int m = 0;
        for (int i = 0; i < n; i++)
        {
            double sw = pl.GetStartWidthAt(i), ew = pl.GetEndWidthAt(i);
            if (sw > 0 || ew > 0)
            {
                sum += (sw + ew) / 2;
                m++;
            }
        }
        if (m == 0)
            return 0;
        cw = sum / m;
        ctx.Diag.Add("構造SVG書き出し: 可変幅ポリラインの平均幅近似",
                     FormattableString.Invariant($"幅 {cw:0.###}（図面単位）で代表"));
        return cw;
    }

    void MapSpline(Spline sp, Ctx ctx, List<IrNode> sink)
    {
        var p = NewPath(sp, ctx, stroke: true);
        var nd = sp.NurbsData;
        var ctrlPts = nd.GetControlPoints();
        var knots = nd.GetKnots();
        var ctrl = new List<(double X, double Y)>(ctrlPts.Count);
        foreach (Point3d cp in ctrlPts)
            ctrl.Add((cp.X, cp.Y));
        var kn = new List<double>(knots.Count);
        foreach (double u in knots)
            kn.Add(u);

        if (!nd.Periodic && CurveApprox.TryNurbsToBeziers(nd.Degree, nd.Rational, ctrl, kn,
                                                          p.Segments, ctx.Map, emitMove: true))
        {
            if (nd.Closed)
                p.Segments.Add(new IrClose());
            sink.Add(p);
            EmitLinetypeSymbols(sp, sp, ctx, sink);
            return;
        }

        // 厳密変換できない類型（有理・3次以外・周期）は許容誤差付きの折れ線で近似する
        double t0 = sp.StartParam, t1 = sp.EndParam;
        var start = sp.GetPointAtParameter(t0);
        var s = ctx.Map(start.X, start.Y);
        p.Segments.Add(new IrMove(s.X, s.Y));
        int before = p.Segments.Count;
        CurveApprox.AdaptivePolyline(p.Segments, t =>
        {
            var pt = sp.GetPointAtParameter(t);
            return ctx.Map(pt.X, pt.Y);
        }, t0, t1, AcquireDefaults.SplineToleranceMm);
        ctx.Diag.Add("構造SVG書き出し: スプラインの折れ線近似（ベジェ厳密変換の対象外類型）",
                     FormattableString.Invariant(
                         $"次数{nd.Degree} 有理={nd.Rational} 周期={nd.Periodic} → {p.Segments.Count - before}区間（許容 {AcquireDefaults.SplineToleranceMm}mm）"));
        sink.Add(p);
        EmitLinetypeSymbols(sp, sp, ctx, sink);
    }

    void MapHatch(Hatch hatch, Ctx ctx, List<IrNode> sink, int depth)
    {
        bool solidFill = hatch.IsSolidFill;
        if (hatch.IsGradient)
        {
            MapGradientHatch(hatch, ctx, sink);
            return;
        }

        if (solidFill)
        {
            // 塗り: 全ループを1パスに集約し、島は偶奇規則で抜く
            var st = ResolveStyleFor(hatch, ctx);
            var p = NewPath(hatch, ctx, stroke: false);
            p.Fill = st.Rgb;
            p.FillOpacity = st.Opacity;
            p.FillEvenOdd = true;
            for (int i = 0; i < hatch.NumberOfLoops; i++)
                AppendLoop(hatch.GetLoopAt(i), ctx, p.Segments);
            if (p.Segments.Count > 0)
                sink.Add(p);
            return;
        }

        // パターンハッチ: 分解でパターン線群を得て写像する（画面・プロットと同じ見た目の実体）
        MapByExplode(hatch, ctx, sink, depth, $"HATCH:{hatch.PatternName}");
    }

    void MapGradientHatch(Hatch hatch, Ctx ctx, List<IrNode> sink) => EmitGradient(hatch, ctx, sink, ProductName);

    void EmitGradient(Hatch hatch, Ctx ctx, List<IrNode> sink, string product)
    {
        var boundary = new List<IrSegment>();
        for (int i = 0; i < hatch.NumberOfLoops; i++)
            AppendLoop(hatch.GetLoopAt(i), ctx, boundary);
        var style = ResolveStyleFor(hatch, ctx);
        var stops = new List<GradientStop>();
        try
        {
            foreach (var item in hatch.GetGradientColors())
            {
                var (rgb, _) = FromColorObject(item.get_Color());
                stops.Add(new GradientStop(item.get_Value(), rgb));
            }
        }
        catch (System.Exception ex)
        {
            ctx.Diag.Add(AcquireDiagCategories.GradientInvalidFallback,
                         $"{product} HATCH {hatch.Handle} layer={EffLayer(hatch, ctx)}: {ex.GetType().Name}");
        }

        long available = Math.Min(ctx.GradientBudget.RemainingPixels, AcquireDefaults.GradientMaxPixelsPerImage);
        var request = new GradientHatchRequest
        {
            GradientName = hatch.GradientName,
            AngleRadians = hatch.GradientAngle,
            Shift = hatch.GradientShift,
            Stops = stops,
            Boundary = boundary,
            DocumentFromLocal = ctx.DocumentFromLocal,
            Opacity = style.Opacity,
            RequestedDpi = ctx.GradientDpi,
            MinimumDpi = CadPeta.Geometry.RasterLimits.MinDpi,
            MaxDimensionPx = CadPeta.Geometry.RasterLimits.MaxDimensionPx,
            AvailablePixels = available,
        };
        string target = FormattableString.Invariant(
            $"{product} HATCH {hatch.Handle} layer={EffLayer(hatch, ctx)} {hatch.GradientName} 要求={ctx.GradientDpi:0.##}dpi");
        var metadata = new GradientEmitter.Metadata(EffLayer(hatch, ctx), DxfName(hatch), style.ColorMethod, style.Aci);
        GradientEmitter.Emit(request, ctx.GradientBudget, metadata, target, ctx.Diag, sink,
            color => EmitGradientFallback(hatch, ctx, sink, boundary, color));
    }

    void EmitGradientFallback(Hatch hatch, Ctx ctx, List<IrNode> sink,
                              List<IrSegment> boundary, IrRgb color)
    {
        var style = ResolveStyleFor(hatch, ctx);
        var path = NewPath(hatch, ctx, stroke: false);
        path.Fill = color;
        path.FillOpacity = style.Opacity;
        path.FillEvenOdd = true;
        path.Segments.AddRange(boundary);
        if (path.Segments.Count > 0) sink.Add(path);
    }

    void AppendLoop(HatchLoop loop, Ctx ctx, List<IrSegment> segs)
    {
        if (loop.IsPolyline)
        {
            var verts = loop.Polyline;
            if (verts == null || verts.Count < 2)
                return;
            var first = verts[0];
            var s = ctx.Map(first.Vertex.X, first.Vertex.Y);
            segs.Add(new IrMove(s.X, s.Y));
            for (int i = 0; i < verts.Count - 1; i++)
            {
                var a = verts[i];
                var b = verts[i + 1];
                if (a.Bulge == 0)
                {
                    var q = ctx.Map(b.Vertex.X, b.Vertex.Y);
                    segs.Add(new IrLine(q.X, q.Y));
                }
                else
                {
                    CurveApprox.BulgeArc(segs, ctx.Map, a.Vertex.X, a.Vertex.Y,
                                         b.Vertex.X, b.Vertex.Y, a.Bulge);
                }
            }
            var last = verts[verts.Count - 1];
            if (last.Bulge != 0)
                CurveApprox.BulgeArc(segs, ctx.Map, last.Vertex.X, last.Vertex.Y,
                                     first.Vertex.X, first.Vertex.Y, last.Bulge);
            segs.Add(new IrClose());
            return;
        }

        var curves = loop.Curves;
        if (curves == null || curves.Count == 0)
            return;
        bool moved = false;
        foreach (Curve2d c in curves)
        {
            if (c is LineSegment2d ln)
            {
                var a = ctx.Map(ln.StartPoint.X, ln.StartPoint.Y);
                var b = ctx.Map(ln.EndPoint.X, ln.EndPoint.Y);
                if (!moved)
                {
                    segs.Add(new IrMove(a.X, a.Y));
                    moved = true;
                }
                segs.Add(new IrLine(b.X, b.Y));
            }
            else
            {
                // 円弧・楕円弧・NURBS 境界は許容誤差付きの折れ線で近似する（境界形状は
                // コンテンツ量子未満の誤差に収まるため記録不要の規則的正規化）
                var iv = c.GetInterval();
                var sp = c.EvaluatePoint(iv.LowerBound);
                var s2 = ctx.Map(sp.X, sp.Y);
                if (!moved)
                {
                    segs.Add(new IrMove(s2.X, s2.Y));
                    moved = true;
                }
                CurveApprox.AdaptivePolyline(segs, t =>
                {
                    var pt = c.EvaluatePoint(t);
                    return ctx.Map(pt.X, pt.Y);
                }, iv.LowerBound, iv.UpperBound, AcquireDefaults.SplineToleranceMm);
            }
        }
        if (moved)
            segs.Add(new IrClose());
    }

    void MapWipeout(Wipeout wipeout, Ctx ctx, List<IrNode> sink)
    {
        // ワイプアウトは「下を隠す白塗り」として写像する（PDF印刷経路のプロット結果と同じ表現。
        // 白はモノクロ化でも維持され、隠蔽機能が保たれる）
        var p = new IrPath
        {
            Fill = new IrRgb(1, 1, 1),
            DataLayer = EffLayer(wipeout, ctx),
            DataEntity = DxfName(wipeout),
        };
        var verts = wipeout.GetVertices();
        if (verts.Count < 3)
            return;
        for (int i = 0; i < verts.Count; i++)
        {
            var (x, y) = ctx.Map(verts[i].X, verts[i].Y);
            p.Segments.Add(i == 0 ? new IrMove(x, y) : (IrSegment)new IrLine(x, y));
        }
        p.Segments.Add(new IrClose());
        sink.Add(p);
    }

    /// <summary>ブロックを展開するグループ（ブロック参照・寸法）の、スタイル・実効の画層・ブロック定義。</summary>
    (ResolvedStyle Style, string EffLayer, BlockTableRecord Block) ResolveBlockGroupInputs(Entity owner, ObjectId blockId, Ctx ctx) =>
        (ResolveStyleFor(owner, ctx), EffLayer(owner, ctx), (BlockTableRecord)ctx.Tr.GetObject(blockId, OpenMode.ForRead));

    void MapBlockReference(BlockReference br, Ctx ctx, List<IrNode> sink, int depth)
    {
        if (depth >= MaxExplodeDepth)
        {
            ctx.Diag.Add("構造SVG書き出し: 入れ子深さ上限のスキップ", DxfName(br));
            return;
        }
        var (st, effLayer, btr) = ResolveBlockGroupInputs(br, br.BlockTableRecord, ctx);

        var g = new IrGroup
        {
            Transform = Conjugate(br.BlockTransform, ctx.GeomScale),
            DataLayer = effLayer,
            DataEntity = $"INSERT:{btr.Name}",
            DataColorMethod = st.ColorMethod,
            DataAci = st.Aci,
        };
        var childCtx = ctx.CloneForBlock(g.Transform, effLayer,
            new ByBlockStyle(st.Rgb, st.ColorMethod, st.Aci, st.Opacity, st.DashDu, st.LineweightMm));
        foreach (ObjectId oid in btr)
        {
            if (ctx.Tr.GetObject(oid, OpenMode.ForRead) is not Entity child)
                continue;
            // 属性定義は定数属性だけが実描画される（可変属性は挿入側の属性参照が持つ）
            if (child is AttributeDefinition ad && !ad.Constant)
                continue;
            MapEntity(child, childCtx, g.Children, depth + 1);
        }
        if (g.Children.Count > 0)
            sink.Add(g);

        // 属性参照はブロックローカルではなく挿入側の座標を持つため、グループ外へ写像する
        foreach (ObjectId attId in br.AttributeCollection)
        {
            if (ctx.Tr.GetObject(attId, OpenMode.ForRead) is AttributeReference att)
                MapEntity(att, ctx, sink, depth + 1);
        }
    }

    void MapDimension(Dimension dim, Ctx ctx, List<IrNode> sink, int depth)
    {
        // 寸法は構成要素（線・矢印・文字）を持つ内部ブロックで描画される。
        // 内部ブロックの幾何は寸法と同じ座標系のため恒等変換のグループに展開する
        if (dim.DimBlockId.IsNull)
        {
            MapByExplode(dim, ctx, sink, depth);
            return;
        }
        var (st, effLayer, btr) = ResolveBlockGroupInputs(dim, dim.DimBlockId, ctx);
        var g = new IrGroup
        {
            DataLayer = effLayer,
            DataEntity = DxfName(dim),
            DataColorMethod = st.ColorMethod,
            DataAci = st.Aci,
        };
        var childCtx = ctx.CloneForBlock(IrMatrix.Identity, effLayer,
            new ByBlockStyle(st.Rgb, st.ColorMethod, st.Aci, st.Opacity, st.DashDu, st.LineweightMm));
        foreach (ObjectId oid in btr)
        {
            if (ctx.Tr.GetObject(oid, OpenMode.ForRead) is Entity child)
                MapEntity(child, childCtx, g.Children, depth + 1);
        }
        if (g.Children.Count > 0)
            sink.Add(g);
    }

    void MapByExplode(Entity ent, Ctx ctx, List<IrNode> sink, int depth, string? label = null)
    {
        string name = label ?? DxfName(ent);
        if (depth >= MaxExplodeDepth)
        {
            ctx.Diag.Add("構造SVG書き出し: 入れ子深さ上限のスキップ", name);
            return;
        }
        var parts = new DBObjectCollection();
        try
        {
            ent.Explode(parts);
        }
        catch (SdkException ex)
        {
            ctx.Diag.Add("構造SVG書き出し: 未対応エンティティのスキップ（分解不可）",
                         $"{name}（{ex.ErrorStatus}）");
            return;
        }
        if (parts.Count == 0)
        {
            ctx.Diag.Add("構造SVG書き出し: 未対応エンティティのスキップ（分解結果が空）", name);
            return;
        }

        var g = new IrGroup { DataLayer = EffLayer(ent, ctx), DataEntity = name };
        var st = ResolveStyleFor(ent, ctx);
        var childCtx = ctx.CloneForBlock(IrMatrix.Identity, EffLayer(ent, ctx),
            new ByBlockStyle(st.Rgb, st.ColorMethod, st.Aci, st.Opacity, st.DashDu, st.LineweightMm));
        try
        {
            foreach (DBObject part in parts)
            {
                if (part is Entity pe)
                    MapEntity(pe, childCtx, g.Children, depth + 1);
            }
        }
        finally
        {
            foreach (DBObject part in parts)
                part.Dispose();
        }
        if (g.Children.Count > 0)
        {
            ctx.Diag.Add("構造SVG書き出し: 複合エンティティの分解写像", $"{name} → {g.Children.Count}要素");
            sink.Add(g);
        }
        else
        {
            ctx.Diag.Add("構造SVG書き出し: 未対応エンティティのスキップ（分解後に描画要素なし）", name);
        }
    }

    // ---------- スタイル解決（色・透過・線種・線の太さ） ----------

    internal sealed record ResolvedStyle(IrRgb Rgb, string ColorMethod, int? Aci, double Opacity,
                                         double[]? DashDu, double LineweightMm);

    ResolvedStyle ResolveStyleFor(Entity ent, Ctx ctx)
    {
        var (rgb, method, aci) = ResolveColor(ent, ctx);
        double opacity = ResolveOpacity(ent, ctx);
        var dash = ResolveLinetype(ent, ctx);
        double lw = ResolveLineweight(ent, ctx);
        return new ResolvedStyle(rgb, method, aci, opacity, dash, lw);
    }

    (IrRgb Rgb, string Method, int? Aci) ResolveColor(Entity ent, Ctx ctx)
    {
        var c = ent.Color;
        if (c.IsByLayer)
        {
            var ltr = (LayerTableRecord)ctx.Tr.GetObject(ResolveLayerId(ent, ctx), OpenMode.ForRead);
            var (rgb, aci) = FromColorObject(ltr.Color);
            return (rgb, "bylayer", aci);
        }
        if (c.IsByBlock)
        {
            if (ctx.ByBlock != null)
                return (ctx.ByBlock.Rgb, "byblock", ctx.ByBlock.Aci);
            // ブロック外の ByBlock は既定色（ACI 7）として描画される
            return (AcquireDefaults.Aci7OnWhite, "byblock", 7);
        }
        var (rgb2, aci2) = FromColorObject(c);
        return (rgb2, aci2 != null ? "aci" : "truecolor", aci2);
    }

    /// <summary>MTEXT 断片色など、エンティティ外の色指定の解決
    /// （ByLayer/ByBlock・特殊指定は文脈の実効値を使う）。</summary>
    (IrRgb Rgb, int? Aci) ResolveEntityColor(EntityColor ec, IrRgb inheritRgb, int? inheritAci)
    {
        if (ec.IsByAci)
        {
            if (ec.ColorIndex == 7)
                return (AcquireDefaults.Aci7OnWhite, 7);
            var cv = Color.FromColorIndex(ColorMethod.ByAci, ec.ColorIndex).ColorValue;
            // 255 = 8bit 色成分の最大値（0..1 へ正規化）
            return (new IrRgb(cv.R / 255.0, cv.G / 255.0, cv.B / 255.0), ec.ColorIndex);
        }
        if (ec.IsByColor)
            return (new IrRgb(ec.Red / 255.0, ec.Green / 255.0, ec.Blue / 255.0), null);
        // ByLayer/ByBlock/ByPen 等は親エンティティの実効値を引き継ぐ
        return (inheritRgb, inheritAci);
    }

    static (IrRgb Rgb, int? Aci) FromColorObject(Color c)
    {
        if (c.ColorMethod == ColorMethod.ByAci)
        {
            short aci = c.ColorIndex;
            if (aci == 7)
            {
                // ACI 7 は背景で白黒が反転する表示専用色。白地への貼付では黒
                return (AcquireDefaults.Aci7OnWhite, 7);
            }
            var cv = Color.FromColorIndex(ColorMethod.ByAci, aci).ColorValue;
            // 255 = 8bit 色成分の最大値（0..1 へ正規化）
            return (new IrRgb(cv.R / 255.0, cv.G / 255.0, cv.B / 255.0), aci);
        }
        var v = c.ColorValue;
        return (new IrRgb(v.R / 255.0, v.G / 255.0, v.B / 255.0), null);
    }

    double ResolveOpacity(Entity ent, Ctx ctx)
    {
        var t = ent.Transparency;
        if (t.IsByLayer)
        {
            var ltr = (LayerTableRecord)ctx.Tr.GetObject(ResolveLayerId(ent, ctx), OpenMode.ForRead);
            var lt = ltr.Transparency;
            // 255 = アルファ最大（不透明）。レイヤ側の ByLayer 指定は「透過なし」を意味する
            return lt.IsByAlpha ? lt.Alpha / 255.0 : 1.0;
        }
        if (t.IsByBlock)
            return ctx.ByBlock?.Opacity ?? 1.0;
        return t.IsByAlpha ? t.Alpha / 255.0 : 1.0;
    }

    /// <summary>
    /// エンティティの実効の線種の記録。ByLayer は画層の線種の記録に置き換える。ByBlock は記録をそのまま返し、
    /// <paramref name="byBlock"/> を true にする（ブロック挿入側の線種を引き継ぐ扱いは呼び出し側が決める）。
    /// </summary>
    LinetypeTableRecord EffectiveLinetypeRecord(Entity ent, Ctx ctx, out bool byBlock)
    {
        var ltrRec = (LinetypeTableRecord)ctx.Tr.GetObject(ent.LinetypeId, OpenMode.ForRead);
        byBlock = false;
        if (ltrRec.Name.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
        {
            var layer = (LayerTableRecord)ctx.Tr.GetObject(ResolveLayerId(ent, ctx), OpenMode.ForRead);
            return (LinetypeTableRecord)ctx.Tr.GetObject(layer.LinetypeObjectId, OpenMode.ForRead);
        }
        byBlock = ltrRec.Name.Equals("ByBlock", StringComparison.OrdinalIgnoreCase);
        return ltrRec;
    }

    double[]? ResolveLinetype(Entity ent, Ctx ctx)
    {
        double entScale = ent.LinetypeScale;
        var ltrRec = EffectiveLinetypeRecord(ent, ctx, out bool byBlock);
        if (byBlock)
        {
            // ブロック外の ByBlock は実線として描画される
            if (ctx.ByBlock == null)
                return null;
            return ScaleDash(ctx.ByBlock.DashDu, entScale);
        }
        if (ltrRec.NumDashes == 0)
            return null; // Continuous（実線）

        var pattern = new List<double>(ltrRec.NumDashes);
        for (int i = 0; i < ltrRec.NumDashes; i++)
            pattern.Add(ltrRec.DashLengthAt(i));
        if (pattern.All(d => d == 0))
            return null;

        // 実線区間（正）と間隙（負）の交互列へ正規化する。ドット（0）は視認できる最小長へ置換する。
        // ただし記号（シェイプ・文字）を運ぶ長さ0要素はドットではなく「記号を置くためのペンアップ」
        // のため置換しない（置換すると実在しない点が描かれる＝偽ドット。記号自体は
        // EmitLinetypeSymbols が別途 path として展開し、ここでは「線種内の記号・文字」の省略ログを
        // 出さない＝展開の成否は EmitLinetypeSymbols 側が記号1個ごとに正確に記録する）
        double totalScale = ctx.Db.Ltscale * entScale;
        var alt = new List<double>(pattern.Count + 2);
        bool expectDash = true;
        for (int i = 0; i < pattern.Count; i++)
        {
            double d = pattern[i];
            bool isDash = d >= 0;
            bool asDot = LinetypeSymbolMath.ShouldSubstituteDotLength(d, DashHasSymbol(ltrRec, i));
            double len = (asDot ? AcquireDefaults.DotDashLengthDu : Math.Abs(d)) * totalScale;
            if (isDash != expectDash)
            {
                alt.Add(0);
                expectDash = !expectDash;
            }
            alt.Add(len);
            expectDash = !expectDash;
        }
        // 奇数個は SVG が役割を反転して繰り返すため、長さ0の間隙で偶数化する
        if (alt.Count % 2 == 1)
            alt.Add(0);
        return alt.ToArray();
    }

    static double[]? ScaleDash(double[]? dash, double factor) =>
        dash?.Select(d => d * factor).ToArray();

    /// <summary>線種のダッシュ区間が記号（シェイプ・文字）を持つか。記号を持たない区間への
    /// 問い合わせは API が NotApplicable を投げる仕様のため、それは「記号なし」と読む。</summary>
    static bool DashHasSymbol(LinetypeTableRecord ltr, int index)
    {
        try
        {
            if (ltr.ShapeNumberAt(index) != 0)
                return true;
        }
        catch (SdkException ex)
            when (ex.ErrorStatus == SdkErrorStatus.NotApplicable)
        {
        }
        try
        {
            if (!string.IsNullOrEmpty(ltr.TextAt(index)))
                return true;
        }
        catch (SdkException ex)
            when (ex.ErrorStatus == SdkErrorStatus.NotApplicable)
        {
        }
        return false;
    }

    // ---------- 複雑線種の記号（シェイプ・文字）展開 ----------

    static readonly ShxShapeOutliner ShapeOutliner = new();

    /// <summary>線種パターン中の記号1個の情報。距離はパターン先頭からの累積距離（図面単位、
    /// LTSCALE・個別線種尺度を適用済み）で、曲線距離としてそのまま使える。</summary>
    internal sealed record LinetypeSymbolElem(bool IsText, int ShapeNumber, string? Text,
                                              ObjectId StyleId, double ScaleDu,
                                              double OffsetXDu, double OffsetYDu, double RotationRad,
                                              bool UcsOriented, bool Upright, double StartDu);

    /// <summary>ダッシュ i の記号情報を安全に取得する（NotApplicable は「記号なし」。
    /// DashHasSymbol で存在を確認済みの index に対して呼ぶ）。</summary>
    static int SafeShapeNumberAt(LinetypeTableRecord ltr, int index)
    {
        try { return ltr.ShapeNumberAt(index); }
        catch (SdkException ex)
            when (ex.ErrorStatus == SdkErrorStatus.NotApplicable)
        {
            return 0;
        }
    }

    static string? SafeTextAt(LinetypeTableRecord ltr, int index)
    {
        try { return ltr.TextAt(index); }
        catch (SdkException ex)
            when (ex.ErrorStatus == SdkErrorStatus.NotApplicable)
        {
            return null;
        }
    }

    /// <summary>
    /// エンティティの実効線種に含まれる記号（シェイプ・文字）の一覧とパターン全長
    /// （図面単位、LTSCALE・個別線種尺度を適用済み）を返す。記号を持たない・実線・
    /// ブロック外の ByBlock（記号を引き継ぐ経路を持たない既存規則）は null。
    /// </summary>
    (List<LinetypeSymbolElem> Symbols, double PatternLenDu)? ResolveLinetypeSymbols(Entity ent, Ctx ctx)
    {
        double entScale = ent.LinetypeScale;
        var ltrRec = EffectiveLinetypeRecord(ent, ctx, out bool byBlock);
        if (byBlock)
        {
            // ByBlock はブロック挿入側の数値パターンだけを引き継ぐ既存規則（ResolveLinetype 参照）。
            // 記号を持つ線種を直接 ByBlock 指定する図面は実測でも未確認のため対象外とする
            return null;
        }
        if (ltrRec.NumDashes == 0)
            return null;

        double totalScale = ctx.Db.Ltscale * entScale;
        var symbols = new List<LinetypeSymbolElem>();
        double cumDu = 0;
        for (int i = 0; i < ltrRec.NumDashes; i++)
        {
            double raw = ltrRec.DashLengthAt(i);
            bool hasSymbolHere = DashHasSymbol(ltrRec, i);
            // ResolveLinetype が実効パターン（st.DashDu、端部整列の入力）へ適用するドット置換と
            // 同じ長さを積む。記号を運ぶ要素はドット置換の対象外（既存規則）のため raw のまま
            cumDu += LinetypeSymbolMath.ShouldSubstituteDotLength(raw, hasSymbolHere)
                ? AcquireDefaults.DotDashLengthDu : Math.Abs(raw);
            if (hasSymbolHere)
            {
                int shapeNumber = SafeShapeNumberAt(ltrRec, i);
                string? text = shapeNumber == 0 ? SafeTextAt(ltrRec, i) : null;
                bool isText = shapeNumber == 0 && !string.IsNullOrEmpty(text);
                if (isText || shapeNumber != 0)
                {
                    var off = ltrRec.ShapeOffsetAt(i);
                    symbols.Add(new LinetypeSymbolElem(
                        IsText: isText,
                        ShapeNumber: shapeNumber,
                        Text: text,
                        StyleId: ltrRec.ShapeStyleAt(i),
                        ScaleDu: ltrRec.ShapeScaleAt(i) * totalScale,
                        OffsetXDu: off.X * totalScale,
                        OffsetYDu: off.Y * totalScale,
                        RotationRad: ltrRec.ShapeRotationAt(i),
                        UcsOriented: ltrRec.ShapeIsUcsOrientedAt(i),
                        Upright: LinetypeShapeUprightCompat.ShapeIsUprightAt(ltrRec, i),
                        // 記号の基準点は「記号を運ぶダッシュ要素の終端」（.lin では記号が
                        // 直前要素の後に置かれる独立要素で、DB は直前要素へ折り込む）。
                        // AutoCAD 実機プロットとの突き合わせで確定した: GAS_LINE の文字位置は
                        // (12.7+5.08)×尺度＋Xオフセット から均等な端部整列シフトを引いた値と
                        // 一致し、要素の始端基準では運搬要素長ぶんずれる（2026-07-10 実測）
                        StartDu: cumDu * totalScale));
                }
            }
        }
        if (symbols.Count == 0)
            return null;
        // 正立フラグ（U=）を読めないホスト世代では相対回転として近似する（対応可否と根拠は
        // 各製品の LinetypeShapeUprightCompat.cs）。左向きの区間では記号が上下逆に
        // 描かれ得る近似のため、記録を残す
        if (!LinetypeShapeUprightCompat.IsSupported)
            ctx.Diag.Add("構造SVG書き出し: 線種記号の正立指定(U=)を判定できず相対回転で近似" +
                         $"（この{ProductName}版のAPIに正立取得が無い）", ltrRec.Name);
        return (symbols, ltrRec.PatternLength * totalScale);
    }

    /// <summary>
    /// エンティティの線種に記号（シェイプ・文字）があれば、曲線に沿った出現位置ごとに
    /// path/text 要素をホストの直後（同じ z 順）へ追加する。曲線距離は AutoCAD の Curve API
    /// （GetPointAtDist・GetFirstDerivative）で図面単位のまま求め、ctx.Map で最後にまとめて
    /// mm・Y反転へ写す（他の幾何写像と同じ規則）。
    /// ポリラインは線種生成モード（Plinegen）に従い、オフのとき（既定）はセグメントごとに
    /// パターンを再起動する（AutoCAD の実描画と同じ。連続扱いにすると継ぎ目の記号位置がずれる）。
    /// </summary>
    void EmitLinetypeSymbols(Entity ent, Curve curve, Ctx ctx, List<IrNode> sink)
    {
        // 記号はホスト幾何への付加要素であり、展開の失敗でコマンド全体（ホスト線の出力を含む）を
        // 巻き込んではならない。想定内の失敗は個別の「省略」ログで捕捉済みだが、想定外の実行系
        // 例外（例: ホスト版差による API 欠落の型初期化子失敗を AutoCAD 2024 実機で実測）も
        // ここで堰き止め、理由の全量（連鎖＋スタック）を残して破線のみの出力へ退避する
        try
        {
            EmitLinetypeSymbolsCore(ent, curve, ctx, sink);
        }
        catch (Exception ex)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（想定外のエラー、破線のみ出力）",
                         $"{DxfName(ent)}: {ex}");
        }
    }

    void EmitLinetypeSymbolsCore(Entity ent, Curve curve, Ctx ctx, List<IrNode> sink)
    {
        (List<LinetypeSymbolElem> Symbols, double PatternLenDu)? resolved;
        try
        {
            resolved = ResolveLinetypeSymbols(ent, ctx);
        }
        catch (SdkException ex)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（線種定義を読めず）", $"{DxfName(ent)}: {ex}");
            return;
        }
        if (resolved == null)
            return;
        var (symbols, patternLenDu) = resolved.Value;
        string dxfName = DxfName(ent);
        if (patternLenDu <= 0)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（パターン長が0以下）", dxfName);
            return;
        }

        // Plinegen オフのポリラインはセグメントごとに独立した「開いた曲線」として端部整列する
        // （MapLwPolyline のホスト破線ジオメトリと同じ単位）。それ以外は曲線全体をひとつの単位とし、
        // 閉じている（円・全周楕円・閉ポリライン〔Plinegen オン〕・閉スプライン）なら整数周期への
        // 伸縮、開いていれば全長ぶんの端部整列を適用する（docs/02 §2、実測導出は
        // tests/tools/cad-typography/linetype_symbol_check/justify_derive/）。
        bool perSegmentOpen = ent is SdkPolyline pl0 && !pl0.Plinegen;
        bool wholeClosed = !perSegmentOpen && IsClosedForDash(ent);

        var segments = new List<(double StartDu, double LenDu)>();
        if (perSegmentOpen)
        {
            // Plinegen オフ: 頂点区間ごとにパターンを再起動する。LWPOLYLINE のパラメータは
            // 頂点番号と一致するため、区間 i の距離域は [GetDistanceAtParameter(i), GetDistanceAtParameter(i+1)]
            var pl = (SdkPolyline)ent;
            int n = pl.NumberOfVertices;
            int segCount = pl.Closed ? n : n - 1;
            for (int i = 0; i < segCount; i++)
            {
                double d0 = curve.GetDistanceAtParameter(i);
                double d1 = curve.GetDistanceAtParameter(i + 1);
                segments.Add((d0, d1 - d0));
            }
        }
        else
        {
            double total = curve.GetDistanceAtParameter(curve.EndParam);
            segments.Add((0.0, total));
        }

        var st = ResolveStyleFor(ent, ctx);
        if (st.DashDu is not { Length: > 0 } patternDuAbs)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（実効パターンを解決できず）", dxfName);
            return;
        }
        // パターン長・記号の周期内オフセットは用紙上の絶対値のため、セグメントのローカル長
        // （曲線 API がブロック定義側の単位で返す）と比較する前にグループ拡大率で割る
        // （破線の JustifyDashDu と同じ規則。挿入尺度1では割っても値は変わらない）
        double[] patternDu = patternDuAbs.Select(d => d / ctx.GroupScale).ToArray();
        string effLayer = EffLayer(ent, ctx);
        int placedCount = 0;
        int omittedCount = 0;
        foreach (var (segStartDu, segLenDu) in segments)
        {
            foreach (var sym in symbols)
            {
                double startDu = sym.StartDu / ctx.GroupScale;
                var occurrences = (wholeClosed
                    ? LinetypeJustifyMath.SymbolOccurrencesClosed(patternDu, segLenDu, startDu)
                    : LinetypeJustifyMath.SymbolOccurrences(patternDu, segLenDu, startDu)).ToList();
                if (occurrences.Count == 0)
                {
                    // 区間が短く、この記号の最初の出現位置すら収まらない（フォールバック対象）。
                    // 曲線上の位置解決やフォント解決の失敗は TryPlaceSymbol 側が個別に記録するため
                    // ここで数えるのは「そもそも出現位置が無い」場合に限る
                    omittedCount++;
                    continue;
                }
                foreach (double d in occurrences)
                {
                    if (TryPlaceSymbol(curve, sym, segStartDu + d, st, effLayer, dxfName, ctx, sink))
                        placedCount++;
                }
            }
        }
        if (placedCount > 0)
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開", $"{dxfName} → {placedCount}個");
        if (omittedCount > 0)
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（区間が短く1周期に満たない）",
                         $"{dxfName} → {omittedCount}箇所");
    }

    /// <summary>曲線距離 dist の位置へ記号1個を配置する。曲線上の位置・接線を求められない
    /// （縮退・範囲外等）場合や、シェイプ・文字の描画に失敗した場合は false（呼び出し側が
    /// 「省略」件数へ数える）。</summary>
    bool TryPlaceSymbol(Curve curve, LinetypeSymbolElem sym, double dist, ResolvedStyle st,
                        string effLayer, string dxfName, Ctx ctx, List<IrNode> sink)
    {
        Point3d pt;
        Vector3d deriv;
        try
        {
            pt = curve.GetPointAtDist(dist);
            double param = curve.GetParameterAtDistance(dist);
            deriv = curve.GetFirstDerivative(param);
        }
        catch (SdkException ex)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（曲線上の位置を求められず）", $"{dxfName}: {ex}");
            return false;
        }
        double tlen = Math.Sqrt(deriv.X * deriv.X + deriv.Y * deriv.Y);
        if (tlen <= 0)
            return false; // 縮退接線（尖点等）は記号を置けない

        double tx = deriv.X / tlen, ty = deriv.Y / tlen;
        double nx = -ty, ny = tx; // 接線を90°反時計回りした左法線（AutoCAD の X=接線・Y=左法線 規約）
        // オフセット・寸法は用紙上の絶対値（線の太さと同じ性質）のため、ローカル曲線点 pt へ
        // 足し込む前にグループ拡大率で割る（挿入尺度1では割っても値は変わらない）
        double offXDu = sym.OffsetXDu / ctx.GroupScale;
        double offYDu = sym.OffsetYDu / ctx.GroupScale;
        double baseX = pt.X + offXDu * tx + offYDu * nx;
        double baseY = pt.Y + offXDu * ty + offYDu * ny;

        double tangentAngle = Math.Atan2(ty, tx);
        double finalAngle = LinetypeSymbolMath.ResolveAngle(tangentAngle, sym.RotationRad,
                                                            sym.UcsOriented, sym.Upright);
        var (xMm, yMm) = ctx.Map(baseX, baseY);
        double rotationDeg = -finalAngle * 180.0 / Math.PI; // Y反転に伴う角度符号反転（他の文字回転と同じ規約）

        double scaleDu = sym.ScaleDu / ctx.GroupScale;
        return sym.IsText
            ? CadTextMapper.MapLinetypeText(sym.Text!, sym.StyleId, scaleDu * ctx.GeomScale,
                xMm, yMm, rotationDeg, st, effLayer, dxfName, ctx.GroupScale, ctx.Tr, ctx.TextCtx,
                ctx.Diag, sink)
            : TryPlaceShapeSymbol(sym, xMm, yMm, rotationDeg, st, effLayer, dxfName, ctx, sink);
    }

    /// <summary>シェイプ記号1個を輪郭化して path として sink へ追加する。</summary>
    bool TryPlaceShapeSymbol(LinetypeSymbolElem sym, double xMm, double yMm, double rotationDeg,
                             ResolvedStyle st, string effLayer, string dxfName, Ctx ctx, List<IrNode> sink)
    {
        if (sym.StyleId.IsNull || sym.StyleId.IsErased)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（シェイプのスタイル参照が無効）", dxfName);
            return false;
        }
        var styleRec = (TextStyleTableRecord)ctx.Tr.GetObject(sym.StyleId, OpenMode.ForRead);
        string? shxPath = CadTextMapper.ResolveShxPath(styleRec.FileName, ctx.TextCtx, ctx.Diag);
        if (shxPath == null)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（シェイプフォントを解決できず）",
                         $"{dxfName} font='{styleRec.FileName}'");
            return false;
        }

        List<IrSegment>? raw;
        string? failReason;
        try
        {
            if (!ShapeOutliner.TryOutlineShape(shxPath, sym.ShapeNumber, out raw, out failReason)
                || raw == null)
            {
                ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（シェイプ番号を解決できず）",
                             $"{dxfName} font={Path.GetFileName(shxPath)} 番号={sym.ShapeNumber}: {failReason}");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            ctx.Diag.Add("構造SVG書き出し: 線種記号の展開省略（シェイプフォントの解析に失敗）",
                         $"{dxfName} font={shxPath}: {ex}");
            return false;
        }
        if (raw.Count == 0)
            return true; // 空図形（正常。記号定義が空というだけで欠落ではない）

        // シェイプ設計単位 → mm。文字と異なり above 基準の正規化を行わない
        // （線種の S= はシェイプ設計単位への直接倍率。ShxShapeOutliner のコメント参照）。
        // 記号の寸法は用紙上の絶対値（線の太さと同じ性質）のため、グループ拡大率で割って格納する
        double scaleMm = sym.ScaleDu * ctx.GeomScale / ctx.GroupScale;
        var textSpace = new List<IrSegment>(raw.Count);
        foreach (var s in raw)
        {
            textSpace.Add(s switch
            {
                // Y反転: シェイプ設計単位はY上向き、テキスト空間相当はY下向き
                IrMove m => new IrMove(m.X * scaleMm, -(m.Y * scaleMm)),
                IrLine l => new IrLine(l.X * scaleMm, -(l.Y * scaleMm)),
                IrCubic c => new IrCubic(c.X1 * scaleMm, -(c.Y1 * scaleMm), c.X2 * scaleMm, -(c.Y2 * scaleMm),
                                        c.X * scaleMm, -(c.Y * scaleMm)),
                IrClose => new IrClose(),
                _ => throw new NotSupportedException($"未知の IrSegment 型: {s.GetType()}"),
            });
        }

        var p = new IrPath
        {
            Segments = GlyphPlacement.Place(textSpace, xMm, yMm, rotationDeg, widthFactor: 1.0),
            Stroke = st.Rgb,
            StrokeOpacity = st.Opacity,
            StrokeWidthMm = st.LineweightMm / ctx.GroupScale,
            DataLayer = effLayer,
            DataEntity = dxfName,
            DataColorMethod = st.ColorMethod,
            DataAci = st.Aci,
        };
        sink.Add(p);
        return true;
    }

    double ResolveLineweight(Entity ent, Ctx ctx)
    {
        var lw = ent.LineWeight;
        if (lw == LineWeight.ByLayer)
        {
            var layer = (LayerTableRecord)ctx.Tr.GetObject(ResolveLayerId(ent, ctx), OpenMode.ForRead);
            lw = layer.LineWeight;
        }
        else if (lw == LineWeight.ByBlock)
        {
            if (ctx.ByBlock != null)
                return ctx.ByBlock.LineweightMm;
            lw = LineWeight.ByLineWeightDefault;
        }
        if (lw == LineWeight.ByLineWeightDefault || lw == LineWeight.ByLayer || lw == LineWeight.ByBlock)
        {
            // 既定線の太さ。LWDEFAULT はセッション設定（1/100mm）
            var v = SdkApplication.GetSystemVariable("LWDEFAULT");
            lw = (LineWeight)Convert.ToInt32(v);
        }
        // LineWeight の数値は 1/100mm。0 は最細線のため固定幅へ置換する（PDF印刷経路と同じ規則）
        double mm = (int)lw / 100.0;
        return mm <= 0 ? AcquireDefaults.HairlineMm : mm;
    }

    ObjectId ResolveLayerId(Entity ent, Ctx ctx) => ent.LayerId;

    string EffLayer(Entity ent, Ctx ctx) =>
        ent.Layer == "0" && ctx.BlockLayer != null ? ctx.BlockLayer : ent.Layer;

    static string DxfName(Entity ent) => ent.GetRXClass()?.DxfName is { Length: > 0 } n
        ? n : ent.GetType().Name.ToUpperInvariant();

    /// <summary>ストローク属性を実効値で初期化した IrPath。破線パターンは端部整列
    /// （docs/02 §2、実測導出は tests/tools/cad-typography/linetype_symbol_check/justify_derive/）を
    /// 適用済みの値を持つ。ent がひとつの連続曲線として扱えない場合（Plinegen オフの
    /// ポリライン＝セグメントごとに再整列が必要）は MapLwPolyline 側が個別に処理するため、
    /// applyDashJustify=false でこのメソッドの自動整列を止める。</summary>
    IrPath NewPath(Entity ent, Ctx ctx, bool stroke, bool applyDashJustify = true)
    {
        var st = ResolveStyleFor(ent, ctx);
        var p = new IrPath
        {
            DataLayer = EffLayer(ent, ctx),
            DataEntity = DxfName(ent),
            DataColorMethod = st.ColorMethod,
            DataAci = st.Aci,
        };
        if (stroke)
        {
            p.Stroke = st.Rgb;
            p.StrokeOpacity = st.Opacity;
            // 線の太さは用紙上の絶対幅。グループ変換の拡大率で割って格納し、描画で元へ戻す
            p.StrokeWidthMm = st.LineweightMm / ctx.GroupScale;
            if (applyDashJustify)
                p.StrokeDashMm = JustifyDashDu(ent, st.DashDu, ctx)?.Select(d => d * ctx.GeomScale).ToArray();
        }
        return p;
    }

    /// <summary>
    /// エンティティ全体をひとつの連続曲線とみなして破線パターンへ端部整列を適用する
    /// （開いた曲線=両端の実線片を調整、閉じた曲線=整数周期へ伸縮。docs/02 §2）。
    /// dashDu が実線（null）ならそのまま null。Curve として扱えない stroke 対象は
    /// 現状存在しないため、防御的に無調整（名目パターン）へフォールバックする。
    /// dashDu は用紙上の絶対長（線の太さと同じ性質）だが、曲線長 lengthDu はブロック定義の
    /// ローカル図面単位（グループ変換前）のため、比較の前にグループ拡大率で割って単位を揃える
    /// （揃えないとブロック挿入尺度ぶん周期数がずれる。挿入尺度1では割っても値は変わらない）。
    /// </summary>
    double[]? JustifyDashDu(Entity ent, double[]? dashDu, Ctx ctx)
    {
        if (dashDu is not { Length: > 0 })
            return null;
        var localDash = dashDu.Select(d => d / ctx.GroupScale).ToArray();
        if (ent is not Curve curve)
        {
            ctx.Diag.Add("構造SVG書き出し: 破線の端部整列を省略（曲線として扱えないエンティティ）", DxfName(ent));
            return localDash;
        }
        double lengthDu;
        try
        {
            lengthDu = curve.GetDistanceAtParameter(curve.EndParam);
        }
        catch (SdkException ex)
        {
            ctx.Diag.Add("構造SVG書き出し: 破線の端部整列を省略（曲線長を求められず、名目パターンのまま出力）",
                         $"{DxfName(ent)}: {ex}");
            return localDash;
        }
        var justified = IsClosedForDash(ent)
            ? LinetypeJustifyMath.JustifyClosed(localDash, lengthDu)
            : LinetypeJustifyMath.JustifyOpen(localDash, lengthDu);
        return justified?.Select(Math.Abs).ToArray();
    }

    /// <summary>破線の端部整列で「閉じた曲線」（整数周期へ伸縮）として扱うか。
    /// 円は常に閉じる。楕円・スプラインは全周のときだけ。ポリラインは Closed プロパティに従う
    /// （Plinegen オフのポリラインはここに来ない＝セグメントごとに開いた曲線として扱うため）。</summary>
    static bool IsClosedForDash(Entity ent) => ent switch
    {
        Circle => true,
        Ellipse el => Math.Abs((el.EndParam - el.StartParam) - 2 * Math.PI) < 1e-9,
        SdkPolyline pl => pl.Closed,
        Spline sp => sp.NurbsData.Closed,
        _ => false,
    };

    /// <summary>ブロック変換行列（3D）を IR の2Dアフィンへ、文書写像 D（k倍＋Y反転）で
    /// 共役して写す（T = D·M·D⁻¹）。子ノードが「ローカル座標を D で写した値」を持つ規約と対になる。</summary>
    static IrMatrix Conjugate(Matrix3d m, double k) => new(
        A: m[0, 0], B: -m[1, 0], C: -m[0, 1], D: m[1, 1],
        E: k * m[0, 3], F: -k * m[1, 3]);

    /// <summary>円・円弧の面内基底（半径スケール済み）。法線から OCS の任意軸アルゴリズムで
    /// X 軸を決め、XY 平面へ投影する（平面図では恒等）。</summary>
    static (double Ux, double Uy, double Vx, double Vy) PlaneBasis(Vector3d normal, double radius)
    {
        // AutoCAD の任意軸アルゴリズム: |Nx|,|Ny| < 1/64 なら Ax = Y×N、さもなくば Ax = Z×N
        const double ArbitraryAxisThreshold = 1.0 / 64.0;
        Vector3d ax = Math.Abs(normal.X) < ArbitraryAxisThreshold
                      && Math.Abs(normal.Y) < ArbitraryAxisThreshold
            ? Vector3d.YAxis.CrossProduct(normal)
            : Vector3d.ZAxis.CrossProduct(normal);
        ax = ax.GetNormal();
        Vector3d ay = normal.CrossProduct(ax).GetNormal();
        return (ax.X * radius, ax.Y * radius, ay.X * radius, ay.Y * radius);
    }
}
