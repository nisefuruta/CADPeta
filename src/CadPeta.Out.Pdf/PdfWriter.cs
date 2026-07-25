// 出力アダプタ: IrDocument → 自前の最小PDF（FR-3）。
//
// 確定事項を実装する:
// - 依存パッケージを追加しない（CadPeta.Out.Pdf.csproj 参照）。ストリームは常に無圧縮
//   （3系TFM・実行環境に依らずバイト決定的にするため。EMF が無圧縮24bpp DIBを採るのと同じ判断）
// - MediaBox は IR の物理寸法（mm）を pt へ換算し、用紙実寸を保持する
// - text はフォント埋め込みを行わず、グリフ輪郭ポート（IGlyphOutliner）で輪郭化して path で描く
//   （文字編集性の喪失として診断ログへ記録する非可逆処理）
// - クリップ（IrNode.ClipId）は PDF のクリップパス（W/W* n）で表現する。EMFと異なり
//   PDFは任意パスのクリップをネイティブに持つため、事前クリップ（幾何演算）は不要
// - 透過（ca/CA）は ExtGState でネイティブに表現できるため、EMFと異なり非可逆処理ではない
//   （劣化なし。診断ログの対象外）
// - image は PNG を自前デコードし、無圧縮24bpp RGB の XObject として埋め込む。アルファは
//   SMask（グレースケールの透過マスク）としてネイティブに保持する（PDFが透過を持つ形式で
//   ある以上、白合成で潰すと下に描かれた幾何を隠してしまい、SVG の見た目と食い違うため。
//   ca/CA と同じく劣化なし＝診断ログの対象外）
//
// 座標系: コンテンツストリーム冒頭の cm 一回で mm→pt 変換とY軸反転をまとめて行う。
// それ以降はページ先頭の cm が変換を担うため、パス座標・グループ変換行列は IR の mm 値を
// そのまま書けばよい（EMF の LX/LY 一括変換と同じ考え方）。
// ただし image だけは注意が要る: PDF の画像空間は「データ先頭行が単位正方形の上辺」に写る
// 規約のため、ページ先頭のY反転 cm の下で素直に幅高さ正の cm を書くと上下鏡像になる
// （非対称画像の実レンダで確認済み）。画像の cm は高さを負にして Y+H を原点に取る。
using System.Text;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Out.Pdf;

public sealed class PdfWriter : IIrOutput
{
    readonly IGlyphOutliner _outliner;
    readonly TextWriter? _log;

    /// <param name="textOutliner">グリフ輪郭ポート（合成ルートが GdiGlyphOutliner 等を注入する）。</param>
    /// <param name="log">情報メッセージの出力先（null で沈黙）。診断ログとは別の進行報告。</param>
    public PdfWriter(IGlyphOutliner textOutliner, TextWriter? log = null)
    {
        _outliner = textOutliner ?? throw new ArgumentNullException(nameof(textOutliner));
        _log = log;
    }

    // PDF32000-1 §7.9.5: デフォルトユーザー空間は 1/72 インチ = 1pt。1インチ = 25.4mm（定義、Units.MmPerInch）
    const double PtPerInch = 72.0;
    internal const double PtPerMm = PtPerInch / Units.MmPerInch;

    public void Write(IrDocument ir, string outputPath, IrDiagnostics diag)
    {
        string path = Path.GetFullPath(outputPath);
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);

        var clipDefs = ir.ClipDefs.ToDictionary(c => c.Id);
        var gsNames = new Dictionary<(double ca, double CA), string>();
        var imageObjs = new List<(string Name, byte[] Rgb, byte[]? AlphaGray, int W, int H)>();
        var content = new StringBuilder();
        int nTexts = 0, nTextsDropped = 0, nImages = 0, nImagesWithAlpha = 0;

        string GsName(double ca, double ca2)
        {
            var key = (ca, ca2);
            if (!gsNames.TryGetValue(key, out var name))
                gsNames[key] = name = $"GS{gsNames.Count}";
            return name;
        }

        string RegisterImage(byte[] rgb, byte[]? alphaGray, int w, int h)
        {
            string name = $"Im{imageObjs.Count}";
            imageObjs.Add((name, rgb, alphaGray, w, h));
            return name;
        }

        void EmitPathOps(IEnumerable<IrSegment> segs)
        {
            foreach (var s in segs)
                switch (s)
                {
                    case IrMove m:
                        content.Append(PdfNum.N(m.X)).Append(' ').Append(PdfNum.N(m.Y)).Append(" m\n");
                        break;
                    case IrLine l:
                        content.Append(PdfNum.N(l.X)).Append(' ').Append(PdfNum.N(l.Y)).Append(" l\n");
                        break;
                    case IrCubic c:
                        content.Append(PdfNum.N(c.X1)).Append(' ').Append(PdfNum.N(c.Y1)).Append(' ')
                               .Append(PdfNum.N(c.X2)).Append(' ').Append(PdfNum.N(c.Y2)).Append(' ')
                               .Append(PdfNum.N(c.X)).Append(' ').Append(PdfNum.N(c.Y)).Append(" c\n");
                        break;
                    case IrClose:
                        content.Append("h\n");
                        break;
                    default:
                        throw new InvalidOperationException($"未知のセグメント: {s}");
                }
        }

        // ノード1件の描画を q...Q へ隔離する（色・線幅・破線・gs の状態が兄弟ノードへ
        // 漏れないようにするため）。clipId 指定時はスコープの先頭でクリップパスを設定する
        void WithScope(string? clipId, Action body)
        {
            content.Append("q\n");
            if (clipId != null)
            {
                if (!clipDefs.TryGetValue(clipId, out var def))
                    throw new InvalidOperationException($"クリップ定義が見つからない: id={clipId}（IR不整合）");
                EmitPathOps(def.Segments);
                content.Append(def.EvenOdd ? "W* n\n" : "W n\n");
            }
            body();
            content.Append("Q\n");
        }

        static string Rgb(IrRgb c) => $"{PdfNum.N(c.R)} {PdfNum.N(c.G)} {PdfNum.N(c.B)}";

        void WriteNode(IrNode node)
        {
            switch (node)
            {
                case IrGroup g:
                    WithScope(g.ClipId, () =>
                    {
                        if (!g.Transform.IsIdentity)
                        {
                            var m = g.Transform;
                            content.Append($"{PdfNum.M(m.A)} {PdfNum.M(m.B)} {PdfNum.M(m.C)} " +
                                           $"{PdfNum.M(m.D)} {PdfNum.M(m.E)} {PdfNum.M(m.F)} cm\n");
                        }
                        foreach (var child in g.Children)
                            WriteNode(child);
                    });
                    break;

                case IrPath p:
                    if (p.Fill == null && p.Stroke == null)
                        break; // 塗りも線も無ければ何も描画されない
                    WithScope(p.ClipId, () =>
                    {
                        bool fillTransparent = p.Fill != null && p.FillOpacity < 1.0;
                        bool strokeTransparent = p.Stroke != null && p.StrokeOpacity < 1.0;
                        if (fillTransparent || strokeTransparent)
                            content.Append('/').Append(GsName(
                                p.Fill != null ? p.FillOpacity : 1.0,
                                p.Stroke != null ? p.StrokeOpacity : 1.0)).Append(" gs\n");
                        if (p.Fill != null)
                            content.Append(Rgb(p.Fill.Value)).Append(" rg\n");
                        if (p.Stroke != null)
                        {
                            content.Append(Rgb(p.Stroke.Value)).Append(" RG\n");
                            content.Append(PdfNum.N(p.StrokeWidthMm)).Append(" w\n");
                            if (p.StrokeDashMm is { Length: > 0 } dash)
                                content.Append('[').Append(string.Join(" ", dash.Select(PdfNum.N)))
                                       .Append("] ").Append(PdfNum.N(p.StrokeDashOffsetMm)).Append(" d\n");
                        }
                        EmitPathOps(p.Segments);
                        string op = (p.Fill != null, p.Stroke != null) switch
                        {
                            (true, true) => p.FillEvenOdd ? "B*" : "B",
                            (true, false) => p.FillEvenOdd ? "f*" : "f",
                            (false, true) => "S",
                            (false, false) => "n",
                        };
                        content.Append(op).Append('\n');
                    });
                    break;

                case IrText t:
                {
                    var run = GlyphRun.TrueType(t.Value, t.FontFamily, t.FontSizeMm, t.WidthFactor, t.GlyphXsMm);
                    var textSpace = _outliner.Outline(run);
                    if (textSpace == null)
                    {
                        // 輪郭を取れないランはそのまま諦める（描かず、理由を記録する）。
                        // PDFはフォント埋め込みを行わない方針のため、text要素として残す代替を持たない
                        diag.Add("PDF出力: 輪郭を取れないため文字を省略（情報欠落）",
                                 $"'{t.Value}' font={t.FontFamily}");
                        nTextsDropped++;
                        break;
                    }
                    var placed = GlyphPlacement.Place(textSpace, t.XMm, t.YMm, t.RotationDeg, t.WidthFactor);
                    diag.Add("PDF出力: 文字を輪郭化して埋め込み（フォント埋め込みなし、文字編集性の喪失）",
                             $"'{t.Value}' font={t.FontFamily}");
                    nTexts++;
                    var fill = t.Fill ?? new IrRgb(0, 0, 0);
                    WithScope(t.ClipId, () =>
                    {
                        if (t.FillOpacity < 1.0)
                            content.Append('/').Append(GsName(t.FillOpacity, 1.0)).Append(" gs\n");
                        content.Append(Rgb(fill)).Append(" rg\n");
                        EmitPathOps(placed);
                        content.Append("f\n"); // グリフ輪郭は非ゼロ巻き数で塗る（TextOutlineTransform と同じ規約）
                    });
                    break;
                }

                case IrImage im:
                {
                    var decoded = PdfPngDecoder.Decode(im.PngBytes);
                    var (rgb, alphaGray) = SplitRgbAlpha(decoded);
                    if (alphaGray != null)
                        nImagesWithAlpha++;
                    string name = RegisterImage(rgb, alphaGray, decoded.Width, decoded.Height);
                    WithScope(im.ClipId, () =>
                    {
                        // 画像空間はデータ先頭行が単位正方形の上辺に写る。ページ先頭のY反転 cm の
                        // 下では高さを負にし原点を Y+H に取らないと上下鏡像になる（冒頭コメント参照）
                        content.Append($"{PdfNum.M(im.WidthMm)} 0 0 {PdfNum.M(-im.HeightMm)} " +
                                       $"{PdfNum.M(im.XMm)} {PdfNum.M(im.YMm + im.HeightMm)} cm\n");
                        content.Append('/').Append(name).Append(" Do\n");
                    });
                    nImages++;
                    break;
                }

                default:
                    throw new InvalidOperationException($"未知のノード: {node.GetType().Name}");
            }
        }

        // ---------- コンテンツストリームの組み立て ----------
        double widthPt = ir.WidthMm * PtPerMm, heightPt = ir.HeightMm * PtPerMm;
        content.Append("q\n");
        // mm→pt変換とY軸反転（PDFはY上向き、IRはY下向き）をこの一回の cm にまとめる。
        // 導出: 望む写像は x'=(x-ox)*PtPerMm, y'=Hpt-(y-oy)*PtPerMm。
        // cm の行列 (a b c d e f) は (x,y)→(a·x+c·y+e, b·x+d·y+f) なので
        // a=PtPerMm, d=-PtPerMm, e=-ox·PtPerMm, f=Hpt+oy·PtPerMm となる
        content.Append($"{PdfNum.M(PtPerMm)} 0 0 {PdfNum.M(-PtPerMm)} " +
                       $"{PdfNum.M(-ir.ViewMinXMm * PtPerMm)} {PdfNum.M(heightPt + ir.ViewMinYMm * PtPerMm)} cm\n");
        // ページ全体（viewBox）の恒常クリップ。ノード個別のクリップとは別に、
        // クロップ範囲を常に効かせる（EMF出力の page rect と同じ考え方）
        content.Append($"{PdfNum.N(ir.ViewMinXMm)} {PdfNum.N(ir.ViewMinYMm)} " +
                       $"{PdfNum.N(ir.WidthMm)} {PdfNum.N(ir.HeightMm)} re W n\n");
        foreach (var node in ir.Nodes)
            WriteNode(node);
        content.Append("Q\n");

        // ---------- PDFオブジェクトの組み立て ----------
        var file = new PdfFile();
        int catalogId = file.Allocate();
        int pagesId = file.Allocate();
        int pageId = file.Allocate();

        var latin1 = Encoding.GetEncoding("Latin1");
        int contentId = file.AddStream("", latin1.GetBytes(content.ToString()));

        var gsIds = new Dictionary<string, int>();
        foreach (var kv in gsNames)
            gsIds[kv.Value] = file.AddDict(
                $"/Type /ExtGState /ca {PdfNum.N(kv.Key.ca)} /CA {PdfNum.N(kv.Key.CA)}");

        var imgIds = new Dictionary<string, int>();
        foreach (var (name, rgb, alphaGray, w, h) in imageObjs)
        {
            // アルファは SMask（DeviceGray の透過マスク）としてネイティブに保持する。
            // 本体の RGB はアルファ未適用の生値のまま書く（合成はビューアが行う）
            string smaskRef = "";
            if (alphaGray != null)
            {
                int smaskId = file.AddStream(
                    $"/Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                    "/ColorSpace /DeviceGray /BitsPerComponent 8", alphaGray);
                smaskRef = $" /SMask {smaskId} 0 R";
            }
            imgIds[name] = file.AddStream(
                $"/Type /XObject /Subtype /Image /Width {w} /Height {h} " +
                $"/ColorSpace /DeviceRGB /BitsPerComponent 8{smaskRef}", rgb);
        }

        string resources = "/Resources << " +
            (gsIds.Count > 0
                ? "/ExtGState << " + string.Join(" ", gsIds.Select(kv => $"/{kv.Key} {kv.Value} 0 R")) + " >> "
                : "") +
            (imgIds.Count > 0
                ? "/XObject << " + string.Join(" ", imgIds.Select(kv => $"/{kv.Key} {kv.Value} 0 R")) + " >> "
                : "") +
            ">>";

        file.Add(new PdfObject
        {
            Id = pageId,
            Dict = $"/Type /Page /Parent {pagesId} 0 R " +
                   $"/MediaBox [0 0 {PdfNum.N(widthPt)} {PdfNum.N(heightPt)}] " +
                   $"/Contents {contentId} 0 R {resources}",
        });
        file.Add(new PdfObject { Id = pagesId, Dict = $"/Type /Pages /Kids [{pageId} 0 R] /Count 1" });
        file.Add(new PdfObject { Id = catalogId, Dict = $"/Type /Catalog /Pages {pagesId} 0 R" });

        var bytes = file.Build(catalogId);
        File.WriteAllBytes(path, bytes);

        _log?.WriteLine($"PDF: texts={nTexts} dropped={nTextsDropped} images={nImages} " +
                        $"(SMask付き{nImagesWithAlpha}) mediaBox={widthPt:F2}x{heightPt:F2}pt " +
                        $"({bytes.Length:N0} bytes)");
    }

    /// <summary>デコード済みRGBAを、無圧縮24bpp RGB（トップダウン）と 8bit グレースケールの
    /// アルファ面に分離する。全画素が完全不透明ならアルファ面は null（SMask を書く必要がない）。
    /// RGB はアルファ未適用の生値のまま返す（透過の解釈はビューアの SMask 合成に委ねる）。</summary>
    internal static (byte[] Rgb, byte[]? AlphaGray) SplitRgbAlpha(DecodedPng png)
    {
        const byte Opaque = 255; // 8bitアルファの完全不透明値
        var rgb = new byte[png.Width * png.Height * 3];
        var alpha = new byte[png.Width * png.Height];
        bool hadAlpha = false;
        for (int i = 0, o = 0, ai = 0; i < png.Rgba.Length; i += 4, o += 3, ai++)
        {
            rgb[o] = png.Rgba[i]; rgb[o + 1] = png.Rgba[i + 1]; rgb[o + 2] = png.Rgba[i + 2];
            byte a = png.Rgba[i + 3];
            alpha[ai] = a;
            if (a < Opaque)
                hadAlpha = true;
        }
        return (rgb, hadAlpha ? alpha : null);
    }
}
