using System;
using System.Collections.Generic;
using CadPeta.Ir;
using CadPeta.Transforms;

namespace CadPeta.Gui;

/// <summary>色置換の表の行（置換元・置換先の #RRGGBB と許容差）から、変換の要求に渡す色置換を作る。</summary>
internal static class ColorReplacementRows
{
    /// <summary>
    /// 色変換を使う要求（<paramref name="applyColor"/>）のときだけ行を読み、#RRGGBB でない行か、0～255 の整数でない許容差があれば例外で止める。
    /// 許容差の欄が空なら 0（同じ色だけ）。
    /// 色変換を使わない要求（色を選ぶためのプレビュー）は行を読まずに空を返すので、表に書きかけの行があっても止まらない。
    /// </summary>
    public static IReadOnlyList<ColorReplacement> Parse(IEnumerable<(string? From, string? To, string? Tolerance)> rows, bool applyColor)
    {
        if (!applyColor) return Array.Empty<ColorReplacement>();
        var replacements = new List<ColorReplacement>();
        foreach (var (fromText, toText, toleranceText) in rows)
        {
            if (!IrRgb.TryParseHex(fromText, out var from) || !IrRgb.TryParseHex(toText, out var to))
                throw new ArgumentException($"色置換は #RRGGBB 形式で指定してください: {fromText} → {toText}");
            int tolerance = 0;
            if (!string.IsNullOrWhiteSpace(toleranceText) && ColorReplacement.ParseTolerance(toleranceText!, out tolerance) is { } toleranceError)
                throw new ArgumentException(toleranceError);
            replacements.Add(new(from, to, tolerance));
        }
        return replacements;
    }
}
