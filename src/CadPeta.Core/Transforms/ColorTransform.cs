// 変換層の色変換: モノクロ化・グレースケール化・色の一括置換を path/text の stroke・fill の
// 色値へ適用する。透明度（stroke-opacity/fill-opacity）は色と独立のため触れない。
// 画像（IrImage）は画素が PNG エンコード済みバイト列で、外部ライブラリ参照を持たない内核に
// PNG デコーダを置けないため対象外とし、対象外の事実を診断ログへ記録する。
//
// 色の書き換えは元へ戻せないため、件数と対象を診断ログへ記録する。記録はノード単位ではなく
// 「対象属性×変換前色→変換後色」の集約（件数付き）とする。実物図面では数万ノードの全色に
// 触れる一方、変換は色値ごとに決定的で、ノード単位の記録は診断価値を足さずログを肥大させる。
using System;
using System.Collections.Generic;
using CadPeta.Ir;

namespace CadPeta.Transforms;

/// <summary>色変換モード。Monochrome と Grayscale は排他（合成ルートが検証する）。</summary>
public enum ColorMode
{
    None,
    Monochrome,
    Grayscale,
}

/// <summary>色の一括置換規則。一致判定は 8bit 量子化後（SVG 出力の量子と同一）の R・G・B のそれぞれの差が
/// <paramref name="Tolerance"/> 以内（0 は同じ色だけ。範囲は 0..<see cref="MaxTolerance"/>）。</summary>
public readonly record struct ColorReplacement(IrRgb From, IrRgb To, int Tolerance = 0)
{
    /// <summary>許容差の上限（8bit の成分の差の最大）。</summary>
    public const int MaxTolerance = 255;

    /// <summary>許容差の文字（前後の空白は除く）を数にする。0～<see cref="MaxTolerance"/> の整数でなければ理由を返す（数にできれば null）。</summary>
    public static string? ParseTolerance(string text, out int tolerance)
    {
        if (int.TryParse(text.Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out tolerance)
            && tolerance <= MaxTolerance)
            return null;
        tolerance = 0;
        return $"色置換の許容差は 0～{MaxTolerance} の整数で指定する: {text.Trim()}";
    }

    public bool Matches(IrRgb color)
    {
        var (r, g, b) = color.ToRgb8();
        var (fr, fg, fb) = From.ToRgb8();
        return Math.Max(Math.Abs(r - fr), Math.Max(Math.Abs(g - fg), Math.Abs(b - fb))) <= Tolerance;
    }
}

/// <summary>置換の 1 行に一致した、使用色の一覧の色の数と件数（件数は属性の数）。</summary>
public readonly record struct ReplacementMatch(int Colors, int Count);

public static class ColorTransform
{
    // RGB→グレーの輝度係数。ISO 32000-1:2008 §10.4.2（PDF の RGB→DeviceGray 変換）の式
    // Gray = 0.3R + 0.59G + 0.11B に従う（入力が PDF である本ツールの分野慣行）
    const double GrayWeightR = 0.3;
    const double GrayWeightG = 0.59;
    const double GrayWeightB = 0.11;

    // 8bit 色成分の最大値。モノクロ化で維持する「純白」の量子化判定に使う。ワイプアウトの
    // 白塗りはプロット PDF で純白 RGB(1,1,1) として現れる（実測）。白要素は「背景と同化して
    // 下を隠す・濃色塗りの上で白く抜く」意味を持つため、黒化せずそのまま残す
    const int White8 = 255;

    /// <summary>色変換を適用する。置換はモードより先に評価する（例: 赤→白へ置換してから
    /// モノクロ化すれば、赤だった要素だけを白で残せる）。</summary>
    public static void Apply(IrDocument ir, ColorMode mode,
                             IReadOnlyList<ColorReplacement> replacements, IrDiagnostics diag)
    {
        // 集約記録。初出順を保って diag 出力を実行間で安定させる（キー = 種別 \t 対象）
        var counts = new Dictionary<string, int>();
        var order = new List<string>();

        // seq は深さ優先の通し番号（グループの子も1ノードとして数える）
        int seq = -1;
        void Visit(IEnumerable<IrNode> nodes)
        {
            foreach (var node in nodes)
            {
                seq++;
                switch (node)
                {
                    case IrPath p:
                        p.Stroke = Convert(p.Stroke, "path stroke", mode, replacements, counts, order);
                        p.Fill = Convert(p.Fill, "path fill", mode, replacements, counts, order);
                        break;
                    case IrText t:
                        t.Fill = Convert(t.Fill, "text fill", mode, replacements, counts, order);
                        break;
                    case IrImage:
                        // 冒頭コメントのとおり画像は対象外。件数と対象の記録だけ残す（出現は
                        // SOLIDハッチ×透過の画像化・OLE 画像化の少数類型＝ノード単位で記録できる）
                        diag.Add("色変換の対象外（画像はパススルー）", $"image seq={seq}");
                        break;
                    case IrGroup g:
                        // グループは色属性を持たない。子孫の色を変換する
                        Visit(g.Children);
                        break;
                }
            }
        }
        Visit(ir.Nodes);

        foreach (var key in order)
        {
            int sep = key.IndexOf('\t');
            diag.Add(key.Substring(0, sep), $"{key.Substring(sep + 1)} {counts[key]}件");
        }
    }

    static IrRgb? Convert(IrRgb? color, string attr, ColorMode mode,
                          IReadOnlyList<ColorReplacement> replacements,
                          Dictionary<string, int> counts, List<string> order)
    {
        if (color == null)
            return null;
        var c = color.Value;

        // 1) 色の一括置換。規則は指定順に評価し、最初に一致した1件だけを適用する
        //    （置換結果へ後続規則を連鎖適用しない＝A→B, B→C を並べても A は B で止まる）
        int rule = FirstMatch(c, replacements);
        if (rule >= 0)
        {
            Record(counts, order, "色変換（色の一括置換）", attr, c, replacements[rule].To);
            c = replacements[rule].To;
        }

        // 2) モード適用
        IrRgb after = mode switch
        {
            ColorMode.Monochrome => IsWhite(c) ? c : new IrRgb(0, 0, 0),
            ColorMode.Grayscale => Gray(c),
            _ => c,
        };
        if (SameQuantized(after, c))
            return c;  // 量子化後に不変（黒→黒、グレー→同値グレー等）は元の値を保ち、記録しない
        Record(counts, order,
               mode == ColorMode.Monochrome ? "色変換（モノクロ化）" : "色変換（グレースケール化）",
               attr, c, after);
        return after;
    }

    /// <summary>置換の行ごとに、一致する使用色の数と件数を数える。色が複数の行に一致するときは、置き換えと同じく上の行だけに数える。</summary>
    public static IReadOnlyList<ReplacementMatch> SummarizeMatches(IReadOnlyList<ColorUse> inventory,
                                                                   IReadOnlyList<ColorReplacement> replacements)
    {
        var colors = new int[replacements.Count];
        var uses = new int[replacements.Count];
        foreach (var use in inventory)
        {
            int rule = FirstMatch(use.Color, replacements);
            if (rule < 0) continue;
            colors[rule]++;
            uses[rule] += use.Count;
        }
        var matches = new ReplacementMatch[replacements.Count];
        for (int i = 0; i < matches.Length; i++) matches[i] = new ReplacementMatch(colors[i], uses[i]);
        return matches;
    }

    // 規則は指定順に評価し、最初に一致した 1 件の番号を返す（無ければ -1）
    static int FirstMatch(IrRgb color, IReadOnlyList<ColorReplacement> replacements)
    {
        for (int i = 0; i < replacements.Count; i++)
            if (replacements[i].Matches(color)) return i;
        return -1;
    }

    static IrRgb Gray(IrRgb c)
    {
        double g = GrayWeightR * c.R + GrayWeightG * c.G + GrayWeightB * c.B;
        return new IrRgb(g, g, g);
    }

    static bool IsWhite(IrRgb c)
    {
        var (r, g, b) = c.ToRgb8();
        return r == White8 && g == White8 && b == White8;
    }

    static bool SameQuantized(IrRgb a, IrRgb b) => a.ToRgb8() == b.ToRgb8();

    static void Record(Dictionary<string, int> counts, List<string> order,
                       string type, string attr, IrRgb from, IrRgb to)
    {
        string key = $"{type}\t{attr} {from.ToSvg()}→{to.ToSvg()}";
        if (!counts.ContainsKey(key))
        {
            counts[key] = 0;
            order.Add(key);
        }
        counts[key]++;
    }
}
