// 表示文言カタログ（lang ファイル方式の中立部）。キー＝値の平文（UTF-8、# 行コメント、
// \n・\t・\s・\\ エスケープ）を辞書化し、キー欠落はフォールバック連鎖（同言語の組み込み、
// ja はさらに en）で補う。数値の書式は InvariantCulture に固定する（表示文字列の数値表記を
// 実行環境の地域設定で揺らさない）。ファイルの読込・言語選択は殻側（CadPeta.Plugin.Shared の
// Msg）が担い、ここは BCL だけで完結する。
using System.Globalization;

namespace CadPeta.UseCases;

public sealed class MessageCatalog
{
    readonly IReadOnlyDictionary<string, string> _entries;
    readonly MessageCatalog? _fallback;

    public MessageCatalog(string name, IReadOnlyDictionary<string, string> entries,
                          MessageCatalog? fallback = null)
    {
        Name = name;
        _entries = entries;
        _fallback = fallback;
    }

    /// <summary>カタログ名（LCID 文字列。"1041"＝ja、"1033"＝en）。</summary>
    public string Name { get; }

    public IEnumerable<string> Keys => _entries.Keys;

    // 表示時フォールバックの通知先は、実行コンテキスト（コマンドの実行の流れ）単位の
    // ambient 値で持つ。Format を呼んだ流れの通知先だけへ届くため、独立したコマンド間で
    // 診断が相互配送されない。カタログ横断の共有状態（単一スロット・登録集合）にすると、
    // 文脈の重なりで上書き・誤配送・解除後の遅延呼び出しが避けられない
    static readonly System.Threading.AsyncLocal<Action<string, string, string>?> _formatFallbackSink = new();

    /// <summary>
    /// 現在の実行コンテキストへ表示時フォールバックの通知先を設定する。引数は（失敗した段の
    /// カタログ名・キー・失敗理由）。殻がコマンド開始時にコマンドラインへの報告を設定する
    /// （黙って切り替えない）。返り値の Dispose は直前の通知先へ戻す（using で入れ子順に
    /// 解除する）。設定なしの文脈は通知なし（殻の初期化を通らない経路）。
    /// </summary>
    public static IDisposable BeginFormatFallbackScope(Action<string, string, string> sink)
    {
        var previous = _formatFallbackSink.Value;
        _formatFallbackSink.Value = sink;
        return new FormatFallbackScope(previous);
    }

    sealed class FormatFallbackScope : IDisposable
    {
        readonly Action<string, string, string>? _previous;
        bool _disposed;
        public FormatFallbackScope(Action<string, string, string>? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return; // 多重 Dispose で二重復元しない
            _disposed = true;
            _formatFallbackSink.Value = _previous;
        }
    }

    // 再帰ガードはスレッド単位。インスタンス共有にすると、あるスレッドの通知中に
    // 別スレッドの失敗まで再帰と誤判定して無通知になる
    [ThreadStatic] static bool _notifyingFallback;

    /// <summary>
    /// キーの文言を組み立てる。値は引数の有無に依らず複合書式として展開する（`{{` は `{`）。
    /// キーが無い段と、展開に失敗する段（差し替えた外部ファイルの、引数の型に依存する
    /// 書式不正は読込時に検出できない）はフォールバック連鎖の次の段（組み込みの文言）で
    /// 表示を続け、失敗した段は <see cref="FormatFallbackObserver"/> へ都度通知する。
    /// どの段でも組み立てられなければ "[キー名]" を返して欠陥を表示のまま可視化する
    /// （コマンドを中断させない・黙って空文字にしない）。
    /// </summary>
    public string Format(string key, params object[] args)
    {
        for (var catalog = this; catalog != null; catalog = catalog._fallback)
        {
            if (!catalog._entries.TryGetValue(key, out var template))
                continue;
            try
            {
                return string.Format(CultureInfo.InvariantCulture, template, args);
            }
            catch (FormatException ex)
            {
                // この段の値では組み立てられない。理由を通知し、次の段（組み込み）の文言で続ける
                NotifyFormatFallback(catalog.Name, key, ex.Message);
            }
        }
        return "[" + key + "]";
    }

    static void NotifyFormatFallback(string catalogName, string key, string error)
    {
        var sink = _formatFallbackSink.Value; // この実行の流れの通知先だけへ届ける
        if (sink == null || _notifyingFallback)
            return; // 通知文言自体の組み立て失敗（同一スレッドの再帰）はここで止める
        _notifyingFallback = true;
        try
        {
            sink(catalogName, key, error);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 通知は診断の副次経路であり、その失敗（ログ書き込み不能等）で本文の組み立て
            // （組み込み文言へのフォールバック継続）を中断させない。理由は Trace へ退避する
            System.Diagnostics.Trace.WriteLine(
                $"CadPeta: 言語カタログのフォールバック通知に失敗した（{catalogName}/{key}）: {ex}");
        }
        finally
        {
            _notifyingFallback = false;
        }
    }

    /// <summary>
    /// lang ファイル本文を辞書化する。空行と # 始まりの行は無視。それ以外は最初の = で
    /// キーと値に分ける。不正な行・重複キー・未知のエスケープ・不正な複合書式は
    /// FormatException（呼び出し側が組み込みカタログへ切り替えて理由を報告する。
    /// 黙って行を捨てない）。
    /// </summary>
    public static Dictionary<string, string> Parse(string content)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = content.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;
            int eq = line.IndexOf('=');
            if (eq <= 0)
                throw new FormatException($"lang 行 {i + 1} が key=value 形式でない: {line}");
            var key = line.Substring(0, eq).Trim();
            var value = Unescape(line.Substring(eq + 1), i + 1);
            ValidateTemplate(value, i + 1);
            if (map.ContainsKey(key))
                throw new FormatException($"lang 行 {i + 1} のキーが重複している: {key}");
            map.Add(key, value);
        }
        return map;
    }

    /// <summary>
    /// テンプレートが使う引数番号の集合（`{0}` `{1:N0}` 等の番号。`{{` は除く）。
    /// 差し替えファイルの値が組み込み定義に無い引数番号を使っていないかの検査
    /// （読込側）に使う。中括弧の構文の正しさは ValidateTemplate が先に保証する。
    /// </summary>
    public static IReadOnlyCollection<int> PlaceholderIndices(string template)
    {
        var indices = new SortedSet<int>();
        for (int i = 0; i < template.Length; i++)
        {
            if (template[i] != '{') continue;
            if (i + 1 < template.Length && template[i + 1] == '{') { i++; continue; }
            int j = i + 1, value = 0;
            bool any = false;
            while (j < template.Length && char.IsDigit(template[j]))
            {
                value = value * 10 + (template[j] - '0');
                any = true;
                j++;
            }
            if (any) indices.Add(value);
            i = j - 1;
        }
        return indices;
    }

    // 複合書式の検査用プローブ引数。文字列引数は数値用の書式指定（N0 等）を無視して受けるため、
    // ここで検出するのは中括弧の構文（閉じていない { 等）と引数番号の範囲に限られる。
    // 引数の型に依存する書式不正は Format の実行時フォールバックが受け止める。
    // カタログの引数番号の最大は {12}（窓プロットの verbose 行）で、32 はその余裕。
    static readonly object[] FormatProbeArgs =
        Enumerable.Repeat((object)"0", 32).ToArray();

    /// <summary>
    /// 値を .NET 複合書式のテンプレートとして検証する。構造の不正（閉じていない { 等）は
    /// Format を初めて呼んだ時点（コマンド実行中）ではなく読込時に検出し、呼び出し側が
    /// 組み込みカタログへ切り替えられるようにする。
    /// </summary>
    static void ValidateTemplate(string value, int lineNumber)
    {
        try
        {
            _ = string.Format(CultureInfo.InvariantCulture, value, FormatProbeArgs);
        }
        catch (FormatException ex)
        {
            throw new FormatException($"lang 行 {lineNumber} の複合書式が不正: {value}（{ex.Message}）");
        }
    }

    static string Unescape(string value, int lineNumber)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\') { sb.Append(c); continue; }
            if (i + 1 >= value.Length)
                throw new FormatException($"lang 行 {lineNumber} が \\ で終わっている");
            i++;
            sb.Append(value[i] switch
            {
                'n' => '\n',
                't' => '\t',
                's' => ' ',   // 行末の空白をエディタに落とされないための明示表記
                '\\' => '\\',
                _ => throw new FormatException($"lang 行 {lineNumber} に未知のエスケープ \\{value[i]} がある"),
            });
        }
        return sb.ToString();
    }
}
