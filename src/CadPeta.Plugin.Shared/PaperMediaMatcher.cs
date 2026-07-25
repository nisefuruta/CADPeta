// 用紙の対応付け（4製品で唯一の実装）。
//
// 問題: PETA はページ設定のデバイスだけを既知のベクタ PDF ドライバへ上書きする。用紙の正式名
// （CanonicalMediaName）はデバイス固有のため、上書き後に元の名前が存在することはまず期待できない
// （実測: レイアウトの "A3" は DocuWorks Printer の名前で、AutoCAD PDF の pc3 には無い。
// pc3 側は "ISO_A3_(297.00_x_420.00_MM)" という別名で同じ用紙を持つ）。
// 名前で引いて失敗すると既定用紙（レター）へ落ち、寸法も用紙単位も変わってしまい、
// ページ設定の尺度の意味まで壊れる。
//
// したがって名前ではなく**寸法**で対応付ける。用紙の公称寸法は製品・デバイスに依らず mm で得られる
// （実測確認済み: GetMediaBounds はインチ名の用紙でも mm を返す。ANSI_A → 279.4×215.9mm）。
//
// CAD SDK 型を持ち込まない純粋関数（単体テスト対象）。各ポートはデバイスの用紙一覧と実寸を
// 中立型で集めて渡し、選ばれた名前を設定するだけにする。
namespace CadPeta.Plugin.Shared;

/// <summary>対応付けの候補となる用紙（正式名と公称寸法・印刷可能領域。いずれも mm）。</summary>
public readonly struct PaperMediaCandidate
{
    public PaperMediaCandidate(string canonicalName, double pageWidthMm, double pageHeightMm,
                               double printableWidthMm, double printableHeightMm)
    {
        CanonicalName = canonicalName;
        PageWidthMm = pageWidthMm; PageHeightMm = pageHeightMm;
        PrintableWidthMm = printableWidthMm; PrintableHeightMm = printableHeightMm;
    }

    public string CanonicalName { get; }
    public double PageWidthMm { get; }
    public double PageHeightMm { get; }
    public double PrintableWidthMm { get; }
    public double PrintableHeightMm { get; }
}

public static class PaperMediaMatcher
{
    /// <summary>
    /// 同じ用紙とみなす寸法差（mm）。用紙の公称寸法は名目値（A3=420.00×297.00）だが、図面側の
    /// ページ設定には実測由来の端数が入る（実測: レイアウトの A3 が 297.010651×419.988983）。
    /// 1mm 未満の差は用紙の違いを意味しない一方、隣接規格（A3=297 と B4=257）とは十分に離れている。
    /// </summary>
    public const double SizeToleranceMm = 1.0;

    /// <summary>
    /// レイアウトの用紙寸法に対応する用紙を、対象デバイスの用紙一覧から選ぶ。
    /// 見つからなければ null（呼び出し側は既定用紙のまま進めた事実を報告する）。
    /// </summary>
    /// <param name="paperWidthMm">レイアウトの用紙幅（mm）。</param>
    /// <param name="paperHeightMm">レイアウトの用紙高（mm）。</param>
    /// <param name="candidates">対象デバイスが持つ用紙。</param>
    public static string? SelectByPaperSize(double paperWidthMm, double paperHeightMm,
                                            IEnumerable<PaperMediaCandidate> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        if (paperWidthMm <= 0 || paperHeightMm <= 0)
            return null; // 寸法が取れていない図面は対応付けの手がかりがない

        PaperMediaCandidate? best = null;
        bool bestIsSameOrientation = false;
        foreach (var c in candidates)
        {
            bool same = Matches(c.PageWidthMm, c.PageHeightMm, paperWidthMm, paperHeightMm);
            // 向きが逆でも同じ用紙である（A3 縦・横は同一用紙の置き方違い）。
            // ただし向きが一致する候補を優先し、それが無いときだけ入れ替わりを採る
            bool swapped = !same && Matches(c.PageWidthMm, c.PageHeightMm, paperHeightMm, paperWidthMm);
            if (!same && !swapped) continue;

            if (best == null || (same && !bestIsSameOrientation))
            {
                best = c; bestIsSameOrientation = same;
                continue;
            }
            if (same != bestIsSameOrientation) continue; // 向きの優先が上回るので入れ替えない

            // 同条件なら印刷可能領域が広い方を採る。窓を要求どおりの尺度で載せられる余地が大きく、
            // 余白の分だけ用紙合わせへ強制的に切り替わる事態を避けられる（成果物は窓へ切り抜くため、
            // 用紙の余白そのものは出力に残らない）
            if (Area(c) > Area(best.Value))
                best = c;
        }
        return best?.CanonicalName;
    }

    /// <summary>
    /// 元デバイスとPDFデバイスで同名の用紙があっても、寸法が一致するときだけ採用する。
    /// CanonicalMediaNameはデバイス固有の名前空間なので、名前だけの一致を同一用紙の根拠にしない。
    /// </summary>
    public static string? Resolve(string? sourceCanonicalName, double paperWidthMm, double paperHeightMm,
                                  IEnumerable<PaperMediaCandidate> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        var list = candidates.ToList();
        if (!string.IsNullOrEmpty(sourceCanonicalName))
        {
            var exact = list.FirstOrDefault(c =>
                string.Equals(c.CanonicalName, sourceCanonicalName, StringComparison.OrdinalIgnoreCase)
                && (Matches(c.PageWidthMm, c.PageHeightMm, paperWidthMm, paperHeightMm)
                    || Matches(c.PageWidthMm, c.PageHeightMm, paperHeightMm, paperWidthMm)));
            if (!string.IsNullOrEmpty(exact.CanonicalName)) return exact.CanonicalName;
        }
        return SelectByPaperSize(paperWidthMm, paperHeightMm, list);
    }

    /// <summary>
    /// 同寸法用紙がない場合の決定的な代替。対象寸法を収容する最小用紙を優先し、無ければ
    /// 最大用紙を選ぶ。候補順や端末の既定用紙には依存しない。
    /// </summary>
    public static string? SelectFallback(double paperWidthMm, double paperHeightMm,
                                         IEnumerable<PaperMediaCandidate> candidates)
    {
        if (candidates == null) throw new ArgumentNullException(nameof(candidates));
        var valid = candidates.Where(c => Valid(c.PageWidthMm) && Valid(c.PageHeightMm))
            .OrderBy(c => c.CanonicalName, StringComparer.Ordinal).ToList();
        if (valid.Count == 0) return null;
        var containing = valid.Where(c =>
            Contains(c.PageWidthMm, c.PageHeightMm, paperWidthMm, paperHeightMm)
            || Contains(c.PageHeightMm, c.PageWidthMm, paperWidthMm, paperHeightMm)).ToList();
        return (containing.Count > 0
            ? containing.OrderBy(c => ExactMatches(c.PageWidthMm, c.PageHeightMm, paperWidthMm, paperHeightMm)
                                      || ExactMatches(c.PageHeightMm, c.PageWidthMm, paperWidthMm, paperHeightMm) ? 0 : 1)
                .ThenBy(c => c.PageWidthMm * c.PageHeightMm)
                .ThenByDescending(Area).ThenBy(c => c.CanonicalName, StringComparer.Ordinal)
            : valid.OrderByDescending(c => c.PageWidthMm * c.PageHeightMm)
                .ThenByDescending(Area).ThenBy(c => c.CanonicalName, StringComparer.Ordinal))
            .First().CanonicalName;
    }

    static bool Matches(double w, double h, double targetW, double targetH) =>
        Math.Abs(w - targetW) <= SizeToleranceMm && Math.Abs(h - targetH) <= SizeToleranceMm;

    static bool ExactMatches(double w, double h, double targetW, double targetH) =>
        Math.Abs(w - targetW) <= 1e-6 && Math.Abs(h - targetH) <= 1e-6;

    static bool Contains(double w, double h, double targetW, double targetH) =>
        w + SizeToleranceMm >= targetW && h + SizeToleranceMm >= targetH;

    static double Area(PaperMediaCandidate c) => c.PrintableWidthMm * c.PrintableHeightMm;
    static bool Valid(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value > 0;
}
