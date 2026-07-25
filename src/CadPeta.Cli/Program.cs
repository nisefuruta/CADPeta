// 合成ルート: 取得アダプタ（PdfPig）と出力アダプタ（EMF・クリップボード）をここだけで
// 結線する。ファイル出力の引数体系は既存の検証ハーネスと揃えている。
//
// 使い方: cadpeta <入力.pdf> [<出力.svg>] [オプション]
//   <出力.svg> 無指定: クリップボード登録が既定動作（受領 PDF を色変換して Ctrl+V で貼る
//              動線。EMF は一時フォルダへ出力しパスを表示する。登録に失敗しても EMF
//              ファイルは残るため、Office の挿入で代替できる。SVG・diag.json は書かない）
//   <出力.svg> 指定:   ファイル出力のみ（SVG＋diag.json、--emf で EMF。
//              クリップボード登録は --clipboard 指定時だけ行う）
// --keep-bg:  背景矩形の除去（既定で有効）を無効化する
// --crop:     IR座標（mm、ページ左上原点・Y下向き）の矩形で切り出す
// --margin:   クロップ範囲へ付ける余白（mm。--crop と併用）
// --mono:     モノクロ化（純白は維持・他は黒。--gray と排他）
// --gray:     グレースケール化（ISO 32000-1 §10.4.2 の輝度式）
// --replace-color <#RRGGBB> <#RRGGBB>: 色の一括置換（複数指定可、モードより先に適用）
// --layer-groups: レイヤ別グループ化（z順が変わり見た目が崩れ得るため既定オフ）
// --text-outline: 文字アウトライン化（全 text を輪郭の path へ展開。環境非依存の見た目を
//              優先する場合。既定オフ=フォント保持。文字編集性を失う非可逆処理として
//              処理ログへ記録される）
// --emf:      クラシックGDIのEMFをファイル出力する
// --pdf:      自前の最小PDFをファイル出力する（フォント埋め込みなし。文字は輪郭化して埋め込む）
// --png:      GDI+ で直接ラスタライズしたPNGをファイル出力する
// --png-dpi:  PNG出力のDPI（既定 PngWriter.DefaultDpi）。--png と併用
// --clipboard: EMF をクリップボードへ CF_ENHMETAFILE 登録する（--emf 無指定なら
//              一時フォルダの EMF を使う）
// --diag:     診断モード。変換層の各段の前後で IR を SVG としてダンプする
// 診断ログ（件数と対象）は <出力.svg>.diag.json へ併記する（ファイル出力時のみ）
using System.Globalization;
using System.Text;
using System.Text.Json;
using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;
using CadPeta.UseCases;

const string Usage =
    "使い方: cadpeta <入力.pdf> [<出力.svg>] [--keep-bg] [--crop x0 y0 x1 y1] [--margin m]\n" +
    "        [--mono | --gray] [--replace-color #RRGGBB #RRGGBB]... [--layer-groups]\n" +
    "        [--text-outline] [--emf 出力.emf] [--emf-bg #RRGGBB] [--pdf 出力.pdf] [--png 出力.png] [--png-dpi N]\n" +
    "        [--clipboard] [--diag 診断ディレクトリ]\n" +
    "<出力.svg> 無指定はクリップボード登録が既定動作（EMF は一時フォルダへ出力しパスを表示）";

if (args.Length < 1)
{
    Console.Error.WriteLine(Usage);
    return 1;
}
string inputPdf = args[0];
if (!File.Exists(inputPdf))
{
    Console.Error.WriteLine($"入力 PDF が存在しない: {inputPdf}");
    return 1;
}

// 第2位置引数（--で始まらないトークン）が出力SVG。無ければクリップボード既定動作
string? outputSvg = args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal)
    ? args[1] : null;

bool keepBg = false, mono = false, gray = false, layerGroups = false, clipboard = false;
bool textOutline = false;
bool emfBackgroundSpecified = false;
IrRgb emfBackground = new(1, 1, 1);
string? emfOut = null, diagDir = null, pdfOut = null, pngOut = null;
double pngDpi = PdfPetaDefaults.PngDpi;
RectMm? crop = null;
double margin = 0;
var replacements = new List<ColorReplacement>();
try
{
    for (int i = outputSvg != null ? 2 : 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--keep-bg": keepBg = true; break;
            case "--mono": mono = true; break;
            case "--gray": gray = true; break;
            case "--layer-groups": layerGroups = true; break;
            case "--text-outline": textOutline = true; break;
            case "--clipboard": clipboard = true; break;
            case "--emf": emfOut = NextValue(args, ref i, "--emf には出力パスが必要"); break;
            case "--emf-bg":
                emfBackground = ParseHexColor(NextValue(args, ref i, "--emf-bg には #RRGGBB が必要"));
                emfBackgroundSpecified = true;
                break;
            case "--pdf": pdfOut = NextValue(args, ref i, "--pdf には出力パスが必要"); break;
            case "--png": pngOut = NextValue(args, ref i, "--png には出力パスが必要"); break;
            case "--png-dpi":
                pngDpi = ParseMm(NextValue(args, ref i, "--png-dpi には数値が必要"), "--png-dpi");
                if (pngDpi <= 0)
                    throw new ArgumentException($"--png-dpi は正の値が必要: {pngDpi}");
                break;
            case "--diag": diagDir = NextValue(args, ref i, "--diag には出力ディレクトリが必要"); break;
            case "--margin":
                margin = ParseMm(NextValue(args, ref i, "--margin には mm 値が必要"), "--margin");
                break;
            case "--crop":
            {
                var v = new double[4];
                for (int k = 0; k < 4; k++)
                    v[k] = ParseMm(NextValue(args, ref i, "--crop には x0 y0 x1 y1（mm）が必要"), "--crop");
                crop = new RectMm(v[0], v[1], v[2], v[3]);
                break;
            }
            case "--replace-color":
            {
                var from = ParseHexColor(NextValue(args, ref i, "--replace-color には変換前後の #RRGGBB が2つ必要"));
                var to = ParseHexColor(NextValue(args, ref i, "--replace-color には変換前後の #RRGGBB が2つ必要"));
                replacements.Add(new ColorReplacement(from, to));
                break;
            }
            default:
                Console.Error.WriteLine($"不明なオプション: {args[i]}\n{Usage}");
                return 1;
        }
    }
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
if (crop == null && margin != 0)
{
    Console.Error.WriteLine("--margin は --crop と併用する");
    return 1;
}
if (pngOut == null && pngDpi != PdfPetaDefaults.PngDpi)
{
    Console.Error.WriteLine("--png-dpi は --png と併用する");
    return 1;
}
if (mono && gray)
{
    Console.Error.WriteLine("--mono と --gray は排他（どちらか一方を指定する）");
    return 1;
}
// クリップボード既定動作では EMF が必須成果物。--emf 無指定なら一時フォルダへ出力する
// （登録失敗時に挿入で代替できるよう、一時 EMF も消さずに残してパスを案内する）
bool clipboardMode = outputSvg == null;
if (clipboardMode)
    clipboard = true;
if (emfBackgroundSpecified && emfOut == null && !clipboard)
{
    Console.Error.WriteLine("--emf-bg は --emf またはクリップボード出力と併用する");
    return 1;
}
if (clipboard && emfOut == null)
{
    string tempDir = Path.Combine(Path.GetTempPath(), "cadpeta");
    Directory.CreateDirectory(tempDir);
    emfOut = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputPdf) + ".emf");
}

var outputs = new List<PdfPetaOutput>();
if (outputSvg != null) outputs.Add(new(PdfPetaOutputKind.Svg, outputSvg));
if (emfOut != null) outputs.Add(new(clipboard ? PdfPetaOutputKind.Clipboard : PdfPetaOutputKind.Emf, emfOut));
if (pdfOut != null) outputs.Add(new(PdfPetaOutputKind.Pdf, pdfOut));
if (pngOut != null) outputs.Add(new(PdfPetaOutputKind.Png, pngOut, pngDpi));
var request = new PdfPetaRequest
{
    InputPdfPath = inputPdf, KeepBackground = keepBg, CropRectMm = crop, CropMarginMm = margin,
    ColorMode = mono ? ColorMode.Monochrome : gray ? ColorMode.Grayscale : ColorMode.None,
    ColorReplacements = replacements, TextOutline = textOutline, GroupByLayer = layerGroups,
    EmfBackground = emfBackground, Outputs = outputs,
};
PdfPetaRunOptions? runOptions = null;
if (diagDir != null)
{
    string dumpBase = outputSvg != null ? Path.GetFileNameWithoutExtension(outputSvg) : Path.GetFileNameWithoutExtension(inputPdf);
    var sink = MakeDiagSink(diagDir, dumpBase, Console.Out);
    bool announced = false;
    runOptions = new PdfPetaRunOptions
    {
        DiagnosticStageSink = (ordinal, stageName, ir) =>
        {
            if (!announced)
            {
                Console.WriteLine($"診断モード: 変換層の各段の前後を SVG ダンプする → {diagDir}");
                announced = true;
            }
            sink(ordinal, stageName, ir);
        }
    };
}
PdfPetaResult result;
try
{
    result = PdfPetaPipeline.Run(request, Console.Out, runOptions);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (Exception ex)
{
    // CLI のプロセス境界。未処理のまま落とすと利用者に原因が渡らないため、
    // 内部例外連鎖とスタックの全文を報告して非0終了する（実測: 1bit PNG 非対応時の
    // PDF 出力が未処理例外でクラッシュし、切り分け材料が exit コードしか残らなかった）
    Console.Error.WriteLine($"変換に失敗した: {ex}");
    return 1;
}
if (result.ClipboardError != null)
{
    Console.Error.WriteLine($"クリップボード登録に失敗した。EMF ファイルは出力済み: {result.ClipboardEmfPath}\n" +
        "Office の「挿入 > 図」で当該ファイルを貼り付けて代替できる。再実行すれば再登録を試みる。\n" +
        $"詳細: {result.ClipboardError}");
    return 1;
}
var diagOut = new List<object>();
Console.WriteLine("---- 診断ログ（件数と対象） ----");
foreach (var entry in result.Diagnostics)
{
    Console.WriteLine($"{entry.Type}: {entry.Count}件");
    diagOut.Add(new { type = entry.Type, count = entry.Count, targets = entry.Targets });
}
if (result.Diagnostics.Count == 0) Console.WriteLine("（記録なし）");
if (outputSvg != null)
{
    File.WriteAllText(outputSvg + ".diag.json", JsonSerializer.Serialize(diagOut,
        new JsonSerializerOptions { WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }),
        new UTF8Encoding(false));
    Console.WriteLine($"診断ログ出力: {outputSvg}.diag.json");
}
return 0;

// オプションの値トークンを1つ取り出す。欠けていれば理由付きで引数エラーにする
static string NextValue(string[] args, ref int i, string missingMessage)
{
    if (i + 1 >= args.Length)
        throw new ArgumentException(missingMessage);
    return args[++i];
}

// mm 数値の解析。書式不正は「どのオプションの何が悪いか」を付けて引数エラーにする
static double ParseMm(string token, string optionName)
{
    if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
        throw new ArgumentException($"{optionName} の値を数値として解釈できない: {token}");
    return v;
}

// #RRGGBB → IrRgb（0..1）。書式不正は具体例付きで引数エラーにする
static IrRgb ParseHexColor(string token) => IrRgb.ParseHex(token);

// 診断シンク（ファイル IO は合成ルートが担い、内核・ユースケース層に IO を置かない方針）。
// 各段の境界で IR を SVG 直列化し
// <base>.stageNN.<段名>.svg へ書き出す。診断は付随機能のため、ディレクトリ作成不可・書き込み
// 拒否・直列化失敗は理由をログして本処理（SVG/EMF/diag.json）を続行する（握りつぶさない）。
static Action<int, string, IrDocument> MakeDiagSink(string dir, string baseName, TextWriter log)
{
    bool enabled;
    try
    {
        Directory.CreateDirectory(dir);
        enabled = true;
    }
    catch (Exception ex) when (IsDumpFailure(ex))
    {
        log.WriteLine($"診断ディレクトリを作成できないため診断を無効化して続行する: {dir}（{ex.Message}）");
        enabled = false;
    }
    return (ordinal, stageName, ir) =>
    {
        if (!enabled)
            return;
        var path = Path.Combine(dir, DiagnosticDump.FileName(baseName, ordinal, stageName));
        try
        {
            // 診断ダンプだけが出所メタデータ（data-*）を残す。
            // 通常の SVG 出力（上の File.WriteAllText）は既定＝除去のまま
            File.WriteAllText(path, SvgWriter.Write(ir, includeProvenance: true), new UTF8Encoding(false));
            log.WriteLine($"診断ダンプ: {path}");
        }
        catch (Exception ex) when (IsDumpFailure(ex))
        {
            // この段のダンプ失敗は本処理を巻き込まない。理由をログして次段・本出力へ進む
            log.WriteLine($"診断ダンプに失敗（この段のみ飛ばす）: {path}（{ex.Message}）");
        }
    };
}

// 診断ダンプで「その出力先は使えない／IR を直列化できない」を意味する例外の判別。
// これ以外（OutOfMemory 等の実行系異常）は握りつぶさず上へ通す。
// InvalidOperationException は SvgWriter が IR プロファイル外の未知ノードを検出した場合。
static bool IsDumpFailure(Exception ex) =>
    ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
       or NotSupportedException or ArgumentException or InvalidOperationException;
