// 言語カタログの保管と読込（lang ファイル方式）。カタログは 1041.lang（ja）/ 1033.lang（en）の
// 2ファイルで、この DLL の隣の lang フォルダ → 組み込みリソースの順に読む（lang フォルダは
// 再ビルドなしの文言差し替え・言語追加のための口）。現在のカタログは実行コンテキスト
// （コマンドの実行の流れ）単位の ambient 値で持ち、未初期化の文脈の既定は ja（従来文言）。
// どの言語を使うか（ホスト言語ヒントの写像）の判定は殻側（CadPeta.Plugin.Shared の Msg）が担う。
using System.Text;

namespace CadPeta.UseCases;

public static class MessageCatalogs
{
    public const string JapaneseLcid = "1041";
    public const string EnglishLcid = "1033";

    /// <summary>lang ファイルをまとめる、DLL の隣のサブフォルダ名。</summary>
    const string LangSubdirName = "lang";

    static readonly Lazy<MessageCatalogSlot> _english = new(() => Load(EnglishLcid, fallback: null));
    static readonly Lazy<MessageCatalogSlot> _japanese = new(() => Load(JapaneseLcid, fallback: _english!.Value.Catalog));

    // 言語の選択は実行コンテキスト単位の ambient 値。単一の static にすると、コマンド文脈が
    // 入れ子・並行に重なったとき後続の初期化が先行の言語選択を上書きし、終了しても復元されない
    static readonly System.Threading.AsyncLocal<MessageCatalog?> _current = new();

    public static MessageCatalog English => _english.Value.Catalog;
    public static MessageCatalog Japanese => _japanese.Value.Catalog;

    /// <summary>読込の診断情報付きスロット（どこから読んだか・欠落キー）。殻の初期化報告が使う。</summary>
    public static MessageCatalogSlot EnglishSlot => _english.Value;
    public static MessageCatalogSlot JapaneseSlot => _japanese.Value;

    /// <summary>現在の実行コンテキストのカタログ。未初期化の文脈は ja（従来文言）。</summary>
    public static MessageCatalog Current => _current.Value ?? Japanese;

    /// <summary>
    /// 現在の実行コンテキストのカタログを設定する。返り値の Dispose は直前の選択への復元
    /// （using で入れ子順に解除する）。
    /// </summary>
    public static IDisposable BeginLanguageScope(MessageCatalog catalog)
    {
        var previous = _current.Value;
        _current.Value = catalog;
        return new LanguageScope(previous);
    }

    sealed class LanguageScope : IDisposable
    {
        readonly MessageCatalog? _previous;
        bool _disposed;
        public LanguageScope(MessageCatalog? previous) => _previous = previous;
        public void Dispose()
        {
            if (_disposed) return; // 多重 Dispose で二重復元しない
            _disposed = true;
            _current.Value = _previous;
        }
    }

    static MessageCatalogSlot Load(string lcid, MessageCatalog? fallback) =>
        LoadFromDir(AssemblyLangDir(), lcid, fallback);

    /// <summary>lang フォルダを指定して読み込む（テストからは一時フォルダで差し替え時の
    /// 挙動を検査する）。外部ファイルを採用する場合も同言語の組み込みカタログを
    /// フォールバックに連ねる。差し替えファイルのキー欠落を検出・報告し、欠落キーを
    /// 組み込みの文言（ja はさらに en へ連鎖）で補うため。</summary>
    public static MessageCatalogSlot LoadFromDir(string? langDir, string lcid, MessageCatalog? fallback)
    {
        string fileName = lcid + ".lang";
        var embeddedEntries = LoadEmbedded(fileName);
        var embedded = new MessageCatalog(lcid, embeddedEntries, fallback);
        var filePath = langDir == null ? fileName : Path.Combine(langDir, fileName);

        string? fileError = null;
        if (langDir != null && File.Exists(filePath))
        {
            try
            {
                var entries = MessageCatalog.Parse(File.ReadAllText(filePath, Encoding.UTF8));
                ValidateAgainstBuiltIn(entries, embeddedEntries);
                var missing = embeddedEntries.Keys.Where(k => !entries.ContainsKey(k))
                    .OrderBy(k => k, StringComparer.Ordinal).ToList();
                return new MessageCatalogSlot(new MessageCatalog(lcid, entries, embedded),
                                              filePath, fromEmbedded: false, fileError: null, missing);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.Security.SecurityException or FormatException)
            {
                fileError = ex.Message; // 理由を保持し、殻の初期化が組み込みへの切替と併せて報告する
            }
        }
        return new MessageCatalogSlot(embedded, filePath, fromEmbedded: true, fileError,
                                      missingKeys: Array.Empty<string>());
    }

    /// <summary>
    /// 外部ファイルの値が、同キーの組み込み定義に無い引数番号を使っていないことを検査する
    /// （例: 引数が {0} の1個だけのキーへ {1} を書くと、表示時に必ず失敗する）。違反は
    /// ファイル全体を不読として組み込みへ切り替える。組み込みに無い追加キーは呼び出し側の
    /// 引数を持たないため、構造の検証（Parse）だけに留める。
    /// </summary>
    static void ValidateAgainstBuiltIn(IReadOnlyDictionary<string, string> entries,
                                       IReadOnlyDictionary<string, string> builtIn)
    {
        foreach (var pair in entries)
        {
            if (!builtIn.TryGetValue(pair.Key, out var builtInTemplate)) continue;
            var allowed = MessageCatalog.PlaceholderIndices(builtInTemplate);
            foreach (var index in MessageCatalog.PlaceholderIndices(pair.Value))
                if (!allowed.Contains(index))
                    throw new FormatException(
                        $"キー {pair.Key} の複合書式が組み込み定義と不整合（引数 {{{index}}} は渡されない）");
        }
    }

    static string? AssemblyLangDir()
    {
        try
        {
            var location = typeof(MessageCatalogs).Assembly.Location;
            if (string.IsNullOrEmpty(location)) return null; // 単一ファイル等は組み込みへ
            var dir = Path.GetDirectoryName(location);
            return dir == null ? null : Path.Combine(dir, LangSubdirName);
        }
        catch (NotSupportedException)
        {
            return null; // Location を持たない読込形態は組み込みへ
        }
    }

    static Dictionary<string, string> LoadEmbedded(string fileName)
    {
        var assembly = typeof(MessageCatalogs).Assembly;
        using var stream = assembly.GetManifestResourceStream("CadPeta.UseCases.lang." + fileName)
            ?? throw new InvalidOperationException($"組み込み言語カタログが無い: {fileName}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return MessageCatalog.Parse(reader.ReadToEnd());
    }
}

/// <summary>カタログの読込結果（本体と、どこから読んだか・欠落キーの診断情報）。</summary>
public sealed class MessageCatalogSlot
{
    public MessageCatalogSlot(MessageCatalog catalog, string filePath, bool fromEmbedded,
                              string? fileError, IReadOnlyList<string> missingKeys)
    {
        Catalog = catalog; FilePath = filePath; FromEmbedded = fromEmbedded;
        FileError = fileError; MissingKeys = missingKeys;
    }

    public MessageCatalog Catalog { get; }
    public string FilePath { get; }
    public bool FromEmbedded { get; }
    public string? FileError { get; }

    /// <summary>同言語の組み込みカタログにあって外部ファイルに無いキー（辞書順）。
    /// 欠落の判定基準は組み込みのキー集合であり、フォールバック連鎖の他言語カタログは
    /// 比較に使わない（外部 en の追加キーを ja の欠落として誤報しないため）。</summary>
    public IReadOnlyList<string> MissingKeys { get; }
}
