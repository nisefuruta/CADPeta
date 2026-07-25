// SHX 解釈器（CadPeta.Glyphs.Shx）の AutoCAD 実機突き合わせ用プローブ。
// build_glyph_chart.lsp が書き出した配置 TSV（CP932）を読み、各行の文字列を
// ShxGlyphOutliner.TryOutline で輪郭化し、GlyphPlacement.Place で配置して
// SVG IR（SvgWriter）へ直列化する。TryOutline 失敗時は failReason を表示して異常終了する。
using System.Text;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using CadPeta.UseCases;

// 用紙寸法（mm、A3フルブリード横）。CAD(Y上向き) → SVG(Y下向き) の反転基準。
const double PaperWidthMm = 420.0;
const double PaperHeightMm = 297.0;

// AutoCAD の既定線幅（レイヤ0の既定値）。SHX グリフのストローク幅と見当枠の線幅に用いる。
const double DefaultStrokeWidthMm = 0.25;

if (args.Length < 2)
{
    Console.Error.WriteLine("使い方: ShxGlyphProbe <配置TSVパス> <出力SVGパス> [出力セルTSVパス]");
    return 1;
}

string tsvPath = args[0];
string outSvgPath = args[1];
string? cellsPath = args.Length > 2 ? args[2] : null;

// build_glyph_chart.lsp は AutoLISP の open/write-line でファイルを書くため、
// 実際の文字コードは AutoCAD セッションのコードページ（ja-JP = CP932）になる。
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var cp932 = Encoding.GetEncoding(932);
string[] lines = File.ReadAllLines(tsvPath, cp932);

double svgY(double cadY) => PaperHeightMm - cadY;

var doc = new IrDocument
{
    WidthMm = PaperWidthMm,
    HeightMm = PaperHeightMm,
    ViewMinXMm = 0,
    ViewMinYMm = 0,
};

var black = new IrRgb(0, 0, 0);
var outliner = new ShxGlyphOutliner();
int rowCount = 0;
var cellLines = new List<string> { "row\tcharIndex\tchar\tx0Mm\tx1Mm\tyCadMm\theightMm" };

// 1文字ぶんの自然な字送り幅（mm、幅係数未適用）を求める。ShxGlyphOutliner.TryOutline は
// penX を外部へ返さないため、同じ文字を2つ並べた文字列を輪郭化し、1文字目と2文字目で
// 同一形状の点が advance だけ離れて現れることを使って逆算する（幾何は本物の TryOutline の
// 出力そのものなので近似ではなく厳密。SHX にはグリフ間の字送りに文字対依存のカーニングが
// 無いため、単独文字と文字列中の文字で advance は一致する）。
double AdvanceOfChar(string shxPath, string? bigFontPath, char ch, double heightMm)
{
    var single = GlyphRun.Shx(ch.ToString(), shxPath, bigFontPath, capHeightMm: heightMm,
                               widthFactor: 1.0, glyphXsMm: null);
    if (!outliner.TryOutline(single, out var singleSegs, out _) || singleSegs == null
        || singleSegs.Count == 0)
        return 0.5 * heightMm; // グリフが無い（空白等）: ShxGlyphOutliner のフォールバックに準じる近似

    var doubled = GlyphRun.Shx(new string(ch, 2), shxPath, bigFontPath, capHeightMm: heightMm,
                                widthFactor: 1.0, glyphXsMm: null);
    if (!outliner.TryOutline(doubled, out var doubledSegs, out _) || doubledSegs == null
        || doubledSegs.Count != 2 * singleSegs.Count)
        return 0.5 * heightMm; // 想定外の構造: フォールバック

    int n = singleSegs.Count;
    return FirstX(doubledSegs[n]) - FirstX(doubledSegs[0]);
}

static double FirstX(IrSegment s) => s switch
{
    IrMove m => m.X,
    IrLine l => l.X,
    IrCubic c => c.X1,
    _ => throw new NotSupportedException($"未知の IrSegment 型: {s.GetType()}"),
};

foreach (string rawLine in lines)
{
    if (string.IsNullOrWhiteSpace(rawLine))
        continue;

    string[] f = rawLine.Split('\t');

    if (f[0] == "#frame")
    {
        // #frame\tx0\ty0\tx1\ty1（CAD座標、mm）
        double x0 = double.Parse(f[1], System.Globalization.CultureInfo.InvariantCulture);
        double y0 = double.Parse(f[2], System.Globalization.CultureInfo.InvariantCulture);
        double x1 = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
        double y1 = double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture);

        var frame = new IrPath { Stroke = black, StrokeWidthMm = DefaultStrokeWidthMm };
        frame.Segments.Add(new IrMove(x0, svgY(y0)));
        frame.Segments.Add(new IrLine(x1, svgY(y0)));
        frame.Segments.Add(new IrLine(x1, svgY(y1)));
        frame.Segments.Add(new IrLine(x0, svgY(y1)));
        frame.Segments.Add(new IrClose());
        doc.Nodes.Add(frame);
        continue;
    }

    // style\tshxPath\tbigFontPathまたは空\tx\ty\theightMm\trotationDeg\twidthFactor\ttext
    string style = f[0];
    string shxPath = f[1];
    string? bigFontPath = f[2].Length > 0 ? f[2] : null;
    double x = double.Parse(f[3], System.Globalization.CultureInfo.InvariantCulture);
    double y = double.Parse(f[4], System.Globalization.CultureInfo.InvariantCulture);
    double heightMm = double.Parse(f[5], System.Globalization.CultureInfo.InvariantCulture);
    double rotationDeg = double.Parse(f[6], System.Globalization.CultureInfo.InvariantCulture);
    double widthFactor = double.Parse(f[7], System.Globalization.CultureInfo.InvariantCulture);
    string text = f[8];

    var run = GlyphRun.Shx(text, shxPath, bigFontPath, capHeightMm: heightMm,
                            widthFactor: widthFactor, glyphXsMm: null);
    if (!outliner.TryOutline(run, out var segs, out var failReason))
    {
        Console.Error.WriteLine(
            $"輪郭化に失敗（行={rowCount}, style={style}, text=\"{text}\"）: {failReason}");
        return 2;
    }

    // 挿入点は CAD(Y上向き) → SVG(Y下向き) へ反転する。ShxGlyphOutliner の出力は
    // 既にテキスト空間 Y下向きのため、反転は挿入点だけでよい。
    var placed = GlyphPlacement.Place(segs!, x, svgY(y), rotationDeg, widthFactor);

    var path = new IrPath { Stroke = black, StrokeWidthMm = DefaultStrokeWidthMm };
    path.Segments.AddRange(placed);
    doc.Nodes.Add(path);

    if (cellsPath != null)
    {
        double penX = 0.0;
        for (int ci = 0; ci < text.Length; ci++)
        {
            char ch = text[ci];
            double adv = AdvanceOfChar(shxPath, bigFontPath, ch, heightMm) * widthFactor;
            double cellX0 = x + penX;
            double cellX1 = cellX0 + adv;
            cellLines.Add(string.Join('\t', rowCount, ci, ch,
                cellX0.ToString(System.Globalization.CultureInfo.InvariantCulture),
                cellX1.ToString(System.Globalization.CultureInfo.InvariantCulture),
                y.ToString(System.Globalization.CultureInfo.InvariantCulture),
                heightMm.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            penX += adv;
        }
    }

    rowCount++;
}

File.WriteAllText(outSvgPath, SvgWriter.Write(doc), new UTF8Encoding(false));
if (cellsPath != null)
    File.WriteAllLines(cellsPath, cellLines, new UTF8Encoding(false));
Console.WriteLine($"ShxGlyphProbe: {rowCount} 行を輪郭化し {outSvgPath} へ書き出した。");
return 0;
