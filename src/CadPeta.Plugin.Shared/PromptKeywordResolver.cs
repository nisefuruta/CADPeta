using System;
using System.Collections.Generic;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// CAD SDK が返すキーワード表記を、ポート契約である言語非依存のグローバル名へ変換する。
/// SDK 型や製品固有の状態値は受け取らない。
/// </summary>
public static class PromptKeywordResolver
{
    /// <summary>
    /// グローバル名、ローカル短縮名、表示名のいずれかをグローバル名へ解決する。
    /// グローバル名の先頭に付くスクリプト用の下線も受理する。
    /// </summary>
    public static bool TryResolveGlobal(string? raw,
                                        IReadOnlyList<PromptKeyword> keywords,
                                        out string global)
    {
        global = string.Empty;
        if (string.IsNullOrEmpty(raw))
            return false;
        string value = raw!;

        foreach (var keyword in keywords)
        {
            if (Equals(value, keyword.Global)
                || Equals(value, keyword.Local)
                || Equals(value, keyword.Display)
                || (value[0] == '_' && Equals(value.Substring(1), keyword.Global)))
            {
                global = keyword.Global;
                return true;
            }
        }
        return false;
    }

    /// <summary>解決不能な SDK 戻り値はポート契約違反として明示的に失敗させる。</summary>
    public static string ResolveGlobal(string? raw, IReadOnlyList<PromptKeyword> keywords)
    {
        if (TryResolveGlobal(raw, keywords, out var global))
            return global;
        throw new InvalidOperationException(Msg.Format("prompt.unknown_keyword", raw ?? "<null>"));
    }

    static bool Equals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
