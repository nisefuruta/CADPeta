// 文字の写像（DBText / AttributeReference / MTEXT）の4製品共有本体
// （共有の方式は CadIrAcquirer.cs 冒頭のとおり）。
// - TrueType はフォント名を保持した text 要素にする。font-size は em 単位のため、
//   CAD 文字高さ（キャップハイト基準）からフォントごとの係数で換算する（GdiFontMetrics）
// - SHX（ストロークフォント）は既定で自前解釈器により輪郭化し、線の path として展開する
//   （文字編集性を失う非可逆処理のため処理ログへ記録する）。利用者設定（代替書体オプション）
//   または輪郭を取れない場合（フォント不在等）は代替書体の text で近似し、理由を処理ログへ残す
// - MTEXT は書式断片（フォント・色・位置が確定した実描画単位）へ展開して写像する。
//   背景マスクは本文より先に塗り矩形として出力する（下の図形を隠す描画順を保つ）
// - 斜体・鏡像など text 要素で表現できない変形は近似し、診断ログへ記録する
#if CADPETA_ACAD
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using SdkException = Autodesk.AutoCAD.Runtime.Exception;
namespace CadPeta.Acquire.Acad;
#elif CADPETA_ARES
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using SdkException = Teigha.Runtime.Exception;
namespace CadPeta.Acquire.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.Colors;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.Geometry;
using SdkException = Itjp.Ijcad.Runtime.Exception;
namespace CadPeta.Acquire.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.Colors;
using Teigha.DatabaseServices;
using Teigha.Geometry;
using SdkException = Teigha.Runtime.Exception;
namespace CadPeta.Acquire.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Acquire.Common;
using CadPeta.Glyphs.Shx;
using CadPeta.Ir;
using CadPeta.UseCases;

internal static class CadTextMapper
{
    /// <summary>MTEXT 断片色の解決（ByLayer/ByBlock は親エンティティの実効値へ委譲）。</summary>
    public delegate (IrRgb Rgb, int? Aci) ColorResolver(EntityColor color, IrRgb inheritRgb, int? inheritAci);

    static readonly ShxGlyphOutliner ShxOutliner = new();

    /// <summary>文字写像の設定と、取得1回のあいだ共有する状態（SHX パス解決のキャッシュ）。</summary>
    public sealed class TextMapContext
    {
        /// <summary>true なら SHX 文字を代替書体の text で出力する（利用者設定。既定 false=輪郭化）。</summary>
        public bool ShxSubstitute;
        public Database Db = null!;
        /// <summary>フォント名→解決済み絶対パス（不在は null）。FindFile の繰り返しを避ける。</summary>
        public readonly Dictionary<string, string?> ShxPathCache = new(StringComparer.OrdinalIgnoreCase);
    }

    public static void MapDbText(DBText text, Transaction tr, PointMap map, double geomScale,
                                 CadIrAcquirer.ResolvedStyle style, string effLayer,
                                 string dxfName, double groupScale, TextMapContext tc,
                                 IrDiagnostics diag, List<IrNode> sink)
    {
        if (string.IsNullOrEmpty(text.TextString))
            return;
        var str = (TextStyleTableRecord)tr.GetObject(text.TextStyleId, OpenMode.ForRead);
        bool isShx = string.IsNullOrEmpty(str.Font.TypeFace);

        if (text.Oblique != 0)
            diag.Add("構造SVG書き出し: 文字の傾斜（斜体）の直立近似", $"'{text.TextString}'");
        if (text.IsMirroredInX || text.IsMirroredInY)
            diag.Add("構造SVG書き出し: 鏡像文字の非反転近似", $"'{text.TextString}'");

        var (x, y) = map(text.Position.X, text.Position.Y);
        double widthFactor = text.WidthFactor > 0 ? text.WidthFactor : str.XScale;
        double rotationDeg = -text.Rotation * 180.0 / Math.PI; // CAD の反時計回り正を Y反転で符号反転

        if (isShx && !tc.ShxSubstitute
            && TryOutlineShx(text.TextString, str.FileName, str.BigFontFileName,
                             capHeightMm: text.Height * geomScale, widthFactor, x, y, rotationDeg,
                             $"style={str.Name}", style, effLayer, dxfName, groupScale, tc, diag, sink))
            return;

        var (family, emFactor) = ResolveSubstituteFont(str.Font.TypeFace, str.Font.Bold, str.Font.Italic,
                                                       str.BigFontFileName);
        if (isShx)
            AddSubstituteLog(diag, tc, $"'{text.TextString}' style={str.Name} → {family}");
        sink.Add(new IrText
        {
            Value = text.TextString,
            XMm = x,
            YMm = y,
            FontFamily = family,
            FontSizeMm = text.Height * geomScale * emFactor,
            WidthFactor = widthFactor,
            RotationDeg = rotationDeg,
            Fill = style.Rgb,
            FillOpacity = style.Opacity,
            DataLayer = effLayer,
            DataEntity = dxfName,
            DataColorMethod = style.ColorMethod,
            DataAci = style.Aci,
        });
    }

    public static void MapMText(MText mtext, Transaction tr, PointMap map, double geomScale,
                                CadIrAcquirer.ResolvedStyle style, string effLayer,
                                double groupScale, TextMapContext tc,
                                IrDiagnostics diag, List<IrNode> sink, ColorResolver resolveColor)
    {
        // 背景マスクは本文より先（下）に塗る
        if (mtext.BackgroundFill)
            EmitBackgroundMask(mtext, map, style, effLayer, diag, sink, resolveColor);

        mtext.ExplodeFragments((fragment, _) =>
        {
            if (string.IsNullOrEmpty(fragment.Text))
                return MTextFragmentCallbackStatus.Continue;

            bool isShx = string.IsNullOrEmpty(fragment.TrueTypeFont);
            if (fragment.ObliqueAngle != 0)
                diag.Add("構造SVG書き出し: 文字の傾斜（斜体）の直立近似", $"'{fragment.Text}'");

            var (rgb, aci) = resolveColor(fragment.Color, style.Rgb, style.Aci);
            var (x, y) = map(fragment.Location.X, fragment.Location.Y);
            double widthFactor = fragment.WidthFactor > 0 ? fragment.WidthFactor : 1.0;
            double rotationDeg = -Math.Atan2(fragment.Direction.Y, fragment.Direction.X) * 180.0 / Math.PI;
            var fragStyle = style with { Rgb = rgb, Aci = aci ?? style.Aci };

            if (isShx && !tc.ShxSubstitute
                && TryOutlineShx(fragment.Text, fragment.ShxFont, fragment.BigFont,
                                 capHeightMm: fragment.CapsHeight * geomScale, widthFactor, x, y,
                                 rotationDeg, "MTEXT断片", fragStyle, effLayer, "MTEXT",
                                 groupScale, tc, diag, sink))
                return MTextFragmentCallbackStatus.Continue;

            var (family, emFactor) = ResolveSubstituteFont(fragment.TrueTypeFont, fragment.Bold,
                                                           fragment.Italic, fragment.BigFont);
            if (isShx)
                AddSubstituteLog(diag, tc, $"'{fragment.Text}' → {family}");
            sink.Add(new IrText
            {
                Value = fragment.Text,
                XMm = x,
                YMm = y,
                FontFamily = family,
                FontSizeMm = fragment.CapsHeight * geomScale * emFactor,
                WidthFactor = widthFactor,
                RotationDeg = rotationDeg,
                Fill = rgb,
                FillOpacity = style.Opacity,
                DataLayer = effLayer,
                DataEntity = "MTEXT",
                DataColorMethod = style.ColorMethod,
                DataAci = aci ?? style.Aci,
            });
            return MTextFragmentCallbackStatus.Continue;
        }, null);
    }

    /// <summary>
    /// 複雑線種に埋め込まれた文字記号1個を写像する。DBText と同じ規則
    /// （SHX は既定で輪郭化、利用者設定または輪郭化失敗時は代替書体の text）に従うが、
    /// エンティティを介さず線種のダッシュ要素から直接呼ばれるため引数は生値で受け取る。
    /// 展開できた（輪郭化・代替のいずれか）ら true、スタイル参照が無効等で描画要素を
    /// 出せなければ false（呼び出し側が「省略」として診断ログへ記録する）。
    /// </summary>
    public static bool MapLinetypeText(string value, ObjectId styleId, double capHeightMm,
                                       double xMm, double yMm, double rotationDeg,
                                       CadIrAcquirer.ResolvedStyle style, string effLayer, string dxfName,
                                       double groupScale, Transaction tr, TextMapContext tc,
                                       IrDiagnostics diag, List<IrNode> sink)
    {
        if (string.IsNullOrEmpty(value) || capHeightMm <= 0)
            return false;
        if (styleId.IsNull || styleId.IsErased)
            return false;
        var str = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
        bool isShx = string.IsNullOrEmpty(str.Font.TypeFace);
        // 線種の文字記号は widthFactor を持たない（S= は高さのみを指定する）。
        // スタイル側の XScale（DBText の既定と同じ規則）だけを幅係数として使う
        double widthFactor = str.XScale > 0 ? str.XScale : 1.0;

        if (isShx && !tc.ShxSubstitute
            && TryOutlineShx(value, str.FileName, str.BigFontFileName, capHeightMm, widthFactor,
                             xMm, yMm, rotationDeg, $"線種記号 style={str.Name}", style, effLayer, dxfName,
                             groupScale, tc, diag, sink))
            return true;

        var (family, emFactor) = ResolveSubstituteFont(str.Font.TypeFace, str.Font.Bold, str.Font.Italic,
                                                       str.BigFontFileName);
        if (isShx)
            AddSubstituteLog(diag, tc, $"'{value}' 線種記号 style={str.Name} → {family}");
        sink.Add(new IrText
        {
            Value = value,
            XMm = xMm,
            YMm = yMm,
            FontFamily = family,
            FontSizeMm = capHeightMm * emFactor,
            WidthFactor = widthFactor,
            RotationDeg = rotationDeg,
            Fill = style.Rgb,
            FillOpacity = style.Opacity,
            DataLayer = effLayer,
            DataEntity = dxfName,
            DataColorMethod = style.ColorMethod,
            DataAci = style.Aci,
        });
        return true;
    }

    // ---------- SHX 輪郭化 ----------

    /// <summary>
    /// SHX 文字を自前解釈器で輪郭化し、線の path として sink へ追加する。
    /// フォント不在・グリフ不在等で輪郭を取れない場合は false（呼び出し側が代替書体へ
    /// フォールバックする）。その理由は処理ログへ記録する。
    /// </summary>
    static bool TryOutlineShx(string value, string? shxFileName, string? bigFontFileName,
                              double capHeightMm, double widthFactor, double xMm, double yMm,
                              double rotationDeg, string sourceLabel,
                              CadIrAcquirer.ResolvedStyle style, string effLayer, string dxfName,
                              double groupScale, TextMapContext tc, IrDiagnostics diag,
                              List<IrNode> sink)
    {
        string? shxPath = ResolveShxPath(shxFileName, tc, diag);
        if (shxPath == null)
        {
            diag.Add("構造SVG書き出し: SHX文字のフォント代替（フォントファイルを解決できず近似）",
                     $"'{value}' {sourceLabel} font='{shxFileName}'");
            return false;
        }
        string? bigPath = null;
        if (!string.IsNullOrEmpty(bigFontFileName))
        {
            bigPath = ResolveShxPath(bigFontFileName, tc, diag);
            if (bigPath == null)
            {
                diag.Add("構造SVG書き出し: SHX文字のフォント代替（ビッグフォントを解決できず近似）",
                         $"'{value}' {sourceLabel} bigfont='{bigFontFileName}'");
                return false;
            }
        }

        var run = GlyphRun.Shx(value, shxPath, bigPath, capHeightMm, widthFactor, glyphXsMm: null);
        List<IrSegment>? textSpace;
        string? failReason;
        try
        {
            if (!ShxOutliner.TryOutline(run, out textSpace, out failReason) || textSpace == null)
            {
                diag.Add("構造SVG書き出し: SHX文字のフォント代替（輪郭を取れず近似）",
                         $"'{value}' {sourceLabel}: {failReason}");
                return false;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            // SHX ファイルの読み取り・解析の失敗。文脈と例外連鎖を残して代替書体で続行する
            diag.Add("構造SVG書き出し: SHX文字のフォント代替（フォント解析に失敗し近似）",
                     $"'{value}' {sourceLabel} font={shxPath}: {ex}");
            return false;
        }

        if (textSpace.Count == 0)
            return true; // 空白のみ等、描画要素なし（正常。何も出力しない）

        var p = new IrPath
        {
            Segments = GlyphPlacement.Place(textSpace, xMm, yMm, rotationDeg, widthFactor),
            Stroke = style.Rgb,
            StrokeOpacity = style.Opacity,
            // 線量りは用紙上の絶対幅。グループ変換の拡大率で割って格納し、描画で元へ戻す
            //（幾何エンティティと同じ規則）。線種の破線は文字へ適用しない（CAD の文字描画は
            // 線種に関わらず実線のため StrokeDashMm を設定しない）
            StrokeWidthMm = style.LineweightMm / groupScale,
            DataLayer = effLayer,
            DataEntity = dxfName,
            DataColorMethod = style.ColorMethod,
            DataAci = style.Aci,
        };
        sink.Add(p);
        diag.Add("構造SVG書き出し: SHX文字の輪郭化（文字編集性の喪失）",
                 $"'{value}' {sourceLabel} font={Path.GetFileName(shxPath)}" +
                 (bigPath != null ? $"+{Path.GetFileName(bigPath)}" : ""));
        return true;
    }

    /// <summary>
    /// SHX フォント名を絶対パスへ解決する（AutoCAD のフォント検索パス）。不在は null。
    /// 解決結果は取得1回のあいだキャッシュする（同名フォントの FindFile 繰り返しを避ける）。
    /// </summary>
    /// <summary>SHX/シェイプフォント名の絶対パス解決。DBText/MTEXT の文字だけでなく、
    /// 複雑線種に埋め込まれたシェイプ記号のフォント（TextStyleTableRecord.FileName）解決にも使う。</summary>
    internal static string? ResolveShxPath(string? fileName, TextMapContext tc, IrDiagnostics diag)
    {
        if (string.IsNullOrEmpty(fileName))
            return null;
        if (tc.ShxPathCache.TryGetValue(fileName!, out var cached))
            return cached;

        string? resolved = null;
        try
        {
            if (Path.IsPathRooted(fileName) && File.Exists(fileName))
            {
                resolved = fileName;
            }
            else
            {
                // スタイルには拡張子なしの名前も保存され得るため .shx を補って探す
                string name = fileName!.EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
                    ? fileName! : fileName + ".shx";
                string found = HostApplicationServices.Current.FindFile(
                    name, tc.Db, FindFileHint.FontFile);
                if (!string.IsNullOrEmpty(found))
                    resolved = found;
            }
        }
        catch (SdkException)
        {
            // FindFile は不在をエラーステータスの例外で返す。不在は想定内のため
            // ここでは null とし、理由の記録は呼び出し側（対象文字の文脈を持つ側）が行う
            resolved = null;
        }
        tc.ShxPathCache[fileName!] = resolved;
        return resolved;
    }

    static void AddSubstituteLog(IrDiagnostics diag, TextMapContext tc, string target)
    {
        // 利用者設定による代替と、輪郭化を試みた末の代替（TryOutlineShx 内で理由を記録済み）を
        // 書き分ける。後者はここでは重ねて記録しない
        if (tc.ShxSubstitute)
            diag.Add("構造SVG書き出し: SHX文字のフォント代替（利用者設定による近似）", target);
    }

    static void EmitBackgroundMask(MText mtext, PointMap map, CadIrAcquirer.ResolvedStyle style,
                                   string effLayer, IrDiagnostics diag, List<IrNode> sink,
                                   ColorResolver resolveColor)
    {
        // マスク矩形 = 本文の実寸（ActualWidth/Height）＋縁の余白。
        // 余白は AutoCAD の定義どおり（境界オフセット係数 − 1）×文字高さ
        double w = mtext.ActualWidth, h = mtext.ActualHeight;
        if (w <= 0 || h <= 0)
            return;
        double margin = (mtext.BackgroundScaleFactor - 1.0) * mtext.TextHeight;

        // 挿入基点（Attachment）から矩形のローカル範囲を決める（CAD 座標・Y上向き）
        double x0 = mtext.Attachment switch
        {
            AttachmentPoint.TopLeft or AttachmentPoint.MiddleLeft or AttachmentPoint.BottomLeft => 0,
            AttachmentPoint.TopCenter or AttachmentPoint.MiddleCenter or AttachmentPoint.BottomCenter => -w / 2,
            _ => -w,
        };
        double y1 = mtext.Attachment switch
        {
            AttachmentPoint.TopLeft or AttachmentPoint.TopCenter or AttachmentPoint.TopRight => 0,
            AttachmentPoint.MiddleLeft or AttachmentPoint.MiddleCenter or AttachmentPoint.MiddleRight => h / 2,
            _ => h,
        };
        double lx0 = x0 - margin, lx1 = x0 + w + margin;
        double ly1 = y1 + margin, ly0 = y1 - h - margin;

        // 回転させて配置（回転中心は挿入基点）
        double rot = mtext.Rotation;
        double cos = Math.Cos(rot), sin = Math.Sin(rot);
        (double X, double Y) Corner(double lx, double ly) =>
            map(mtext.Location.X + lx * cos - ly * sin,
                mtext.Location.Y + lx * sin + ly * cos);

        IrRgb fill;
        if (mtext.UseBackgroundColor)
        {
            // 「図面の背景色を使う」は貼付先の地＝白として塗る。白の仮定を置く処理のため記録する
            fill = AcquireDefaults.DrawingBackgroundAsWhite;
            diag.Add("構造SVG書き出し: MTEXT背景の「図面の背景色」を白として塗る（貼付先=白地の前提）",
                     $"'{mtext.Text}'");
        }
        else
        {
            (fill, _) = resolveColor(mtext.BackgroundFillColor.EntityColor, style.Rgb, style.Aci);
        }

        var p = new IrPath
        {
            Fill = fill,
            FillOpacity = style.Opacity,
            DataLayer = effLayer,
            DataEntity = "MTEXT:BACKGROUND",
            DataColorMethod = style.ColorMethod,
            DataAci = style.Aci,
        };
        var c0 = Corner(lx0, ly0);
        var c1 = Corner(lx1, ly0);
        var c2 = Corner(lx1, ly1);
        var c3 = Corner(lx0, ly1);
        p.Segments.Add(new IrMove(c0.X, c0.Y));
        p.Segments.Add(new IrLine(c1.X, c1.Y));
        p.Segments.Add(new IrLine(c2.X, c2.Y));
        p.Segments.Add(new IrLine(c3.X, c3.Y));
        p.Segments.Add(new IrClose());
        sink.Add(p);
    }

    /// <summary>代替書体の解決。TrueType は書体名と em 換算係数、SHX は代替書体（近似）を返す。</summary>
    static (string Family, double EmFactor) ResolveSubstituteFont(
        string? typeface, bool bold, bool italic, string? bigFontFile)
    {
        if (!string.IsNullOrEmpty(typeface))
        {
            // 係数計測はスタイルの書体名（GDI が解決する）で行い、IR にはレンダラ非依存の
            // 英語ファミリ名を書く
            string englishName = GdiFontMetrics.EnglishFamilyName(typeface!);
            return (englishName, GdiFontMetrics.EmPerCapHeight(typeface!, bold, italic));
        }
        // SHX（ストロークフォント）。和文対応（ビッグフォント併用）は和文書体で代替する
        string family = string.IsNullOrEmpty(bigFontFile)
            ? AcquireDefaults.ShxSubstituteLatin
            : AcquireDefaults.ShxSubstituteCjk;
        return (family, GdiFontMetrics.EmPerCapHeight(family, bold: false, italic: false));
    }
}
