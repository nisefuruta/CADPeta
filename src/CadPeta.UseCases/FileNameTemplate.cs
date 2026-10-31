using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace CadPeta.UseCases;

/// <summary>PETA の成果物のファイル名に差し込む値。<paramref name="Tab"/> は図面の今のタブの名前（モデルなら Model、レイアウトならその名前。システム変数 CTAB と同じ）。</summary>
public sealed record FileNameFields(string Drawing, string Tab, DateTime Now, string Comment);

/// <summary>ファイル名の形を読んだ結果。使えない形なら <see cref="Template"/> が null で、<see cref="Error"/> に理由が入る。</summary>
public sealed record FileNameTemplateParse(FileNameTemplate? Template, string Error);

/// <summary>
/// PETA の成果物のファイル名（拡張子を除く）の形。差し込みは {drawing}・{tab}・{datetime}・{seq}・{comment} の 5 つで、
/// 設定のファイルと同じく言語で変えない名前にする。差し込みの外の文字はそのまま使う。
/// </summary>
public sealed class FileNameTemplate
{
    public const string Drawing = "{drawing}";
    // モデルとレイアウトのどちらも入るので、AutoCAD が両方をまとめて呼ぶ「タブ」（CTAB）の名前にする
    public const string Tab = "{tab}";
    public const string DateTimeToken = "{datetime}";
    public const string Seq = "{seq}";
    public const string Comment = "{comment}";

    /// <summary>ファイル名の日時の書式（成果物と設定のバックアップの名前の両方に使う）。</summary>
    public const string DateTimeFormat = "yyyyMMdd_HHmmss";
    /// <summary>連番の桁数。</summary>
    const string SeqFormat = "D3";
    /// <summary>差し込みが空のときに詰める区切りの文字。</summary>
    const char Separator = '_';

    static readonly string[] Tokens = { Drawing, Tab, DateTimeToken, Seq, Comment };

    /// <summary>使える差し込み（画面のメニューとツールチップに使う）。</summary>
    public static IReadOnlyList<string> AllTokens => Tokens;

    /// <summary>使える差し込みを空白で区切って並べた文（画面の案内に使う）。</summary>
    public static string TokenList => string.Join(" ", Tokens);
    static readonly Regex TokenPattern = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    /// <summary>今までのファイル名と同じ形（図面名_タブ名_日時）。</summary>
    public static FileNameTemplate Default { get; } = new(Drawing + "_" + Tab + "_" + DateTimeToken);

    FileNameTemplate(string text) => Text = text;

    public string Text { get; }

    public override bool Equals(object? obj) => obj is FileNameTemplate other && other.Text == Text;

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    public override string ToString() => Text;

    /// <summary>PETA の最後にコメントを聞くか（形に {comment} があるときだけ）。</summary>
    public bool UsesComment => Text.Contains(Comment);

    public static FileNameTemplateParse Parse(string? text)
    {
        var catalog = MessageCatalogs.Current;
        if (string.IsNullOrWhiteSpace(text))
            return new FileNameTemplateParse(null, catalog.Format("filename.empty"));
        string value = text!;
        var unknown = TokenPattern.Matches(value).Cast<Match>().Select(m => m.Value).Where(t => !Tokens.Contains(t)).ToArray();
        if (unknown.Length > 0)
            return new FileNameTemplateParse(null, catalog.Format("filename.unknown_token", string.Join(" ", unknown), TokenList));
        string literal = TokenPattern.Replace(value, "");
        if (literal.IndexOf('{') >= 0 || literal.IndexOf('}') >= 0)
            return new FileNameTemplateParse(null, catalog.Format("filename.unbalanced", value));
        char[] invalid = literal.Where(c => Path.GetInvalidFileNameChars().Contains(c)).Distinct().ToArray();
        if (invalid.Length > 0)
            return new FileNameTemplateParse(null, catalog.Format("filename.invalid_chars", Describe(invalid)));
        // 連番は 1 つだけ（2 つあると、どの数字の並びがどの番号か決まらない）
        if (TokenPattern.Matches(value).Cast<Match>().Count(m => m.Value == Seq) > 1)
            return new FileNameTemplateParse(null, catalog.Format("filename.seq_once"));
        // コメントが空のとき（Enter）でも名前が空にならないこと: コメントのほかに差し込みか、区切り以外の文字が要る
        string withoutComment = value.Replace(Comment, "");
        if (!TokenPattern.IsMatch(withoutComment) && withoutComment.Trim().Trim(Separator).Length == 0)
            return new FileNameTemplateParse(null, catalog.Format("filename.comment_only"));
        // コメントがあるときと空のときの両方で、Windows がファイル名として使える名前になること（予約の名前、末尾の . と空白）
        var template = new FileNameTemplate(value);
        foreach (string comment in new[] { "c", "" })
        {
            string? error = ValidateName(template.Expand(new FileNameFields("d", "l", DateTime.MinValue, comment), seq: 1));
            if (error != null) return new FileNameTemplateParse(null, error);
        }
        return new FileNameTemplateParse(template, "");
    }

    /// <summary>ファイル名の形として使えるか。使えなければ理由を返す（使えれば null）。設定の読み込み・-PETAOPT・ダイアログが使う。</summary>
    public static string? Validate(string? text) => Parse(text) is { Template: null } parse ? parse.Error : null;

    /// <summary>既定の形の名前（コメントなし、連番 1）。PETA の一時の成果物と PETADATA の SVG に使う。</summary>
    public static string DefaultName(string drawing, string tab, DateTime now) =>
        Default.Expand(new FileNameFields(drawing, tab, now, ""), seq: 1);

    /// <summary>
    /// コメント（<paramref name="fields"/> の Comment）を受け付けるか。ファイル名に使えない文字を含むか、差し込んだ名前が
    /// Windows のファイル名として使えなければ（<see cref="ValidateName"/>）理由を返す（受け付けるなら null）。
    /// </summary>
    public string? ValidateComment(FileNameFields fields) =>
        fields.Comment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            ? MessageCatalogs.Current.Format("comment.invalid", fields.Comment)
            : ValidateName(Expand(fields, seq: 1));

    // Windows がファイル名として使えない予約の名前（拡張子を付けても使えない）
    static readonly string[] ReservedNames =
        new[] { "CON", "PRN", "AUX", "NUL" }
            .Concat(Enumerable.Range(1, 9).SelectMany(n => new[] { "COM" + n, "LPT" + n }))
            .ToArray();

    /// <summary>
    /// 差し込んだ後の名前（拡張子を除く）を Windows がファイル名として使えるか。使えなければ理由を返す（使えれば null）。
    /// 予約の名前（CON・NUL・COM1 など。拡張子を付けても使えない）と、末尾の . と空白（Windows が消す）を受け付けない。
    /// </summary>
    public static string? ValidateName(string name)
    {
        var catalog = MessageCatalogs.Current;
        if (name.Length == 0)
            return catalog.Format("filename.empty");
        if (name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
            return catalog.Format("filename.trailing_dot_space", name);
        string stem = name.Split('.')[0].TrimEnd(' ');
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase))
            return catalog.Format("filename.reserved", name);
        return null;
    }

    /// <summary>
    /// 形に値を差し込んだファイル名（拡張子を除く）。値が空の差し込みは、隣の区切り（_）を 1 つだけ詰める
    /// （後ろに区切りがあればそれを、無ければ前の区切りを）。そのほかの区切りは、図面名の中のものも形に書いたものも変えない。
    /// </summary>
    public string Expand(FileNameFields fields, int seq)
    {
        var sb = new StringBuilder();
        bool dropNextSeparator = false;
        int position = 0;
        foreach (Match m in TokenPattern.Matches(Text))
        {
            AppendLiteral(Text.Substring(position, m.Index - position));
            string value = m.Value switch
            {
                Drawing => Sanitize(fields.Drawing),
                Tab => Sanitize(fields.Tab),
                DateTimeToken => fields.Now.ToString(DateTimeFormat, CultureInfo.InvariantCulture),
                Seq => seq.ToString(SeqFormat, CultureInfo.InvariantCulture),
                Comment => Sanitize(fields.Comment.Trim()),
                _ => m.Value,
            };
            if (value.Length > 0)
                sb.Append(value);
            else if (m.Index + m.Length < Text.Length && Text[m.Index + m.Length] == Separator)
                dropNextSeparator = true;
            else if (sb.Length > 0 && sb[sb.Length - 1] == Separator)
                sb.Length--;
            position = m.Index + m.Length;
        }
        AppendLiteral(Text.Substring(position));
        return sb.ToString();

        void AppendLiteral(string literal)
        {
            if (dropNextSeparator && literal.Length > 0)
            {
                literal = literal.Substring(1);
                dropNextSeparator = false;
            }
            sb.Append(literal);
        }
    }

    /// <summary>
    /// {seq} の次の番号。<paramref name="existingFileNames"/>（フォルダにあるファイルの名前）のうち、{seq} 以外が同じ値になる名前の
    /// 番号の最大に 1 を足す。無ければ 1。形に {seq} が無ければ 1。既存のファイル名は CADPeta が作ったとは限らないので、
    /// 番号として読めない名前（int に収まらない数、半角でない数字）は無視する（その名前と重なっても、予約の (1) で避ける）。
    /// </summary>
    public int NextSeq(FileNameFields fields, IEnumerable<string> existingFileNames)
    {
        if (!Text.Contains(Seq)) return 1;
        // 連番の位置をファイル名に現れない目印に置き換えて展開し、目印を半角の数字の並びに変えた正規表現で照合する
        const string Marker = "\u0001";
        string expanded = new FileNameTemplate(Text.Replace(Seq, Marker)).Expand(fields, seq: 0);
        string pattern = "^" + Regex.Escape(expanded).Replace(Marker, "([0-9]+)") + @"\.[^.]+$";
        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        int max = 0;
        foreach (string name in existingFileNames)
        {
            var m = regex.Match(name);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > max && n < int.MaxValue)
                max = n;
        }
        return max + 1;
    }

    // 図面名・タブ名・コメントのうち、ファイル名に使えない文字を _ に替える
    static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    static string Describe(IEnumerable<char> chars)
    {
        var sb = new StringBuilder();
        foreach (char c in chars)
            sb.Append(char.IsControl(c) ? string.Format(CultureInfo.InvariantCulture, "U+{0:X4} ", (int)c) : c + " ");
        return sb.ToString().TrimEnd();
    }
}
