// 表示文言の言語解決（殻側）。カタログの保管・読込・ambient な現在値は
// CadPeta.UseCases の MessageCatalogs が持ち、ここは「このホストはどの言語で動いているか」の
// 判定（ヒント列の言語ファミリ写像）と、コマンド開始時の初期化（言語選択・フォールバック
// 通知先の設定・lang ファイル異常の報告）だけを担う。未対応言語・判定不能は 1033（en）へ
// 倒し、その事実をログへ残す（黙って落とさない）。
using System.Globalization;
using System.IO;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

public static class Msg
{
    public const string JapaneseLcid = MessageCatalogs.JapaneseLcid;
    public const string EnglishLcid = MessageCatalogs.EnglishLcid;

    public static MessageCatalog English => MessageCatalogs.English;
    public static MessageCatalog Japanese => MessageCatalogs.Japanese;

    /// <summary>現在の実行コンテキストのカタログ。殻がコマンド開始時に <see cref="Initialize"/> で
    /// 決め、スコープの終了で直前の選択へ戻る。未初期化の文脈は ja（従来文言）。</summary>
    public static MessageCatalog Current => MessageCatalogs.Current;

    public static string Text(string key) => Current.Format(key);
    public static string Format(string key, params object[] args) => Current.Format(key, args);

    /// <summary>
    /// ホスト言語のヒント列（AutoCAD の LOCALE、登録レジストリキーのパス、CurrentUICulture 等。
    /// 取れなかったヒントは null でよい）から、現在の実行コンテキストのカタログを決める。
    /// 判定不能・lang ファイル不読・キー欠落はここで log へ報告する。返り値の Dispose が
    /// 言語選択とフォールバック通知先を直前の状態へ戻すスコープで、殻はコマンドの終了時
    /// （catch のエラー説明まで済んだ後の finally）に解除する。
    /// </summary>
    public static IDisposable Initialize(IEnumerable<string?> hints, TextWriter log)
    {
        string lcid = ChooseLcid(hints, out bool resolved);
        var slot = lcid == JapaneseLcid ? MessageCatalogs.JapaneseSlot : MessageCatalogs.EnglishSlot;
        return InitializeSlot(slot, resolved, log);
    }

    /// <summary>ヒント列からカタログだけを選ぶ（ambient 状態は変えない）。読込フックの
    /// 一度きりの表示など、コマンドスコープの外で使う。</summary>
    public static MessageCatalog ResolveCatalog(IEnumerable<string?> hints) =>
        ChooseLcid(hints, out _) == JapaneseLcid ? Japanese : English;

    /// <summary>言語選択と通知先の設定、初期報告。報告（log.WriteLine）が失敗した場合は、
    /// 呼び出し側がスコープを受け取れず解除できないため、ここで直前の状態へ戻してから
    /// 再送出する。</summary>
    internal static IDisposable InitializeSlot(MessageCatalogSlot slot, bool resolved, TextWriter log)
    {
        var scope = new CommandLanguageScope(
            MessageCatalogs.BeginLanguageScope(slot.Catalog),
            AttachFormatFallbackObserver(slot, log));
        try
        {
            if (!resolved)
                log.WriteLine(slot.Catalog.Format("lang.unknown_host"));
            if (slot.FromEmbedded)
                log.WriteLine(slot.FileError == null
                    ? slot.Catalog.Format("lang.file_missing", slot.FilePath)
                    : slot.Catalog.Format("lang.file_invalid", slot.FilePath, slot.FileError));
            if (slot.MissingKeys.Count > 0)
                log.WriteLine(slot.Catalog.Format("lang.missing_keys",
                    slot.Catalog.Name, slot.MissingKeys.Count, string.Join(", ", slot.MissingKeys)));
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    sealed class CommandLanguageScope : IDisposable
    {
        readonly IDisposable _catalogScope;
        readonly IDisposable _sinkScope;
        bool _disposed;
        public CommandLanguageScope(IDisposable catalogScope, IDisposable sinkScope)
        {
            _catalogScope = catalogScope; _sinkScope = sinkScope;
        }
        public void Dispose()
        {
            if (_disposed) return; // 多重 Dispose で二重復元しない
            _disposed = true;
            _sinkScope.Dispose();
            _catalogScope.Dispose();
        }
    }

    /// <summary>表示時フォールバック（引数の型に依存する書式不正など、読込時に検出できない
    /// 差し替え値の欠陥）を、発生の都度コマンドラインへ報告する結線。現在の実行コンテキストに
    /// だけ効き、返り値の Dispose で直前の通知先へ戻る。</summary>
    internal static IDisposable AttachFormatFallbackObserver(MessageCatalogSlot slot, TextWriter log) =>
        MessageCatalog.BeginFormatFallbackScope((catalogName, key, error) =>
            log.WriteLine(slot.Catalog.Format("lang.format_fallback", catalogName, key, error)));

    /// <summary>lang フォルダを指定した読込（テスト用の委譲。実体は MessageCatalogs）。</summary>
    internal static MessageCatalogSlot LoadFromDir(string? langDir, string lcid, MessageCatalog? fallback) =>
        MessageCatalogs.LoadFromDir(langDir, lcid, fallback);

    /// <summary>最初に言語ファミリへ写像できたヒントで LCID を決める。全滅は en（1033）。</summary>
    internal static string ChooseLcid(IEnumerable<string?> hints, out bool resolved)
    {
        foreach (var hint in hints)
        {
            var lcid = TryMapToLcid(hint);
            if (lcid != null) { resolved = true; return lcid; }
        }
        resolved = false;
        return EnglishLcid;
    }

    /// <summary>
    /// 1ヒントを LCID へ写像する。受け付ける形は2つ:
    /// (1) 言語タグ（"ja"・"ja-JP"・"jpn"・"en"・"en-GB"・"enu" 等）＝先頭の英字列の
    ///     言語ファミリで判定、(2) 末尾が ":16進LCID" のレジストリキーパス
    ///     （AutoCAD 系の "…\ACAD-9101:411" 等）＝プライマリ言語 ID で判定。
    /// どちらでもない・未対応言語は null（呼び出し側が次のヒントへ進む）。
    /// </summary>
    internal static string? TryMapToLcid(string? hint)
    {
        if (string.IsNullOrWhiteSpace(hint)) return null;
        var s = hint!.Trim();

        int colon = s.LastIndexOf(':');
        if (colon >= 0)
        {
            var hex = s.Substring(colon + 1);
            if (hex.Length is > 0 and <= 4
                && int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int lcid))
                return (lcid & 0x3FF) switch   // プライマリ言語 ID（地域差を吸収）
                {
                    0x11 => JapaneseLcid,      // ja
                    0x09 => EnglishLcid,       // en
                    _ => null,
                };
            return null;
        }
        if (s.IndexOfAny(new[] { '\\', '/' }) >= 0) return null; // LCID 無しのパスは判定材料にしない

        int letters = 0;
        while (letters < s.Length && char.IsLetter(s[letters])) letters++;
        if (letters == 0 || (letters < s.Length && s[letters] != '-' && s[letters] != '_'))
            return null;
        return s.Substring(0, letters).ToLowerInvariant() switch
        {
            "ja" or "jpn" or "jp" => JapaneseLcid,
            "en" or "enu" or "eng" => EnglishLcid,
            _ => null,
        };
    }
}
