using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Xml;
using System.Xml.Linq;

namespace CadPeta.UseCases;

/// <summary>設定を読んだ結果の種類。</summary>
public enum SettingsLoadStatus
{
    /// <summary>ファイルが無い。既定を使い、ファイルは作らない。</summary>
    Missing,
    /// <summary>そのまま読めた。</summary>
    Ok,
    /// <summary>一部の値を既定に直して保存した。直す前のファイルを <see cref="SettingsLoadResult.BackupPath"/> に残した。</summary>
    Repaired,
    /// <summary>JSON として読めないので、既定で作り直した。読めなかったファイルを <see cref="SettingsLoadResult.BackupPath"/> に残した。</summary>
    Recreated,
    /// <summary>新しい版の CADPeta が書いた設定。書き換えずに既定を使う。</summary>
    NewerVersion,
    /// <summary>読めない、または直した設定を保存できない。既定（または直した値）を使い、<see cref="SettingsLoadResult.Error"/> に場所と案内を入れる。</summary>
    Unrepairable,
}

/// <summary>設定の 1 つの項目の問題（項目のキーと理由）。</summary>
public sealed record SettingsProblem(string Item, string Reason);

/// <summary>設定を読んだ結果。<see cref="Settings"/> はどの場合も使える値。</summary>
public sealed record SettingsLoadResult(
    CadPetaSettings Settings, SettingsLoadStatus Status, IReadOnlyList<SettingsProblem> Problems, string? BackupPath, string Error)
{
    /// <summary>
    /// 利用者に見せる文（コマンドラインに 1 行ずつ出す）。そのまま読めたときと無いときは空。直した項目と理由、残したファイル、
    /// 使えなかったときの場所と案内は必ず入れる（黙って既定に戻さない）。
    /// </summary>
    public IReadOnlyList<string> Messages()
    {
        if (Status is SettingsLoadStatus.Ok or SettingsLoadStatus.Missing)
            return Array.Empty<string>();
        var catalog = MessageCatalogs.Current;
        var lines = Problems
            .Select(p => p.Item.Length == 0 ? catalog.Format("settings.message", p.Reason) : catalog.Format("settings.problem", p.Item, p.Reason))
            .ToList();
        if (BackupPath != null)
            lines.Add(catalog.Format("settings.backup", BackupPath));
        if (Error.Length > 0)
            lines.Add(catalog.Format("settings.message", Error));
        return lines;
    }
}

/// <summary>
/// PETAOPT の設定のファイル（settings.json）の読み書き。黙って既定に戻さない: 一部の値がおかしければその値だけを既定に直して保存し、
/// JSON として読めなければ既定で作り直す。どちらも直す前のファイルを .bak-<日時> で残し、直した項目と残したファイルを返す。
/// </summary>
public static class CadPetaSettingsStore
{
    /// <summary>データの保存先を変える環境変数（自動の試験が、利用者の設定に触れずに試験の設定を使うため）。</summary>
    public const string DataDirEnvironmentVariable = "CADPETA_DATA_DIR";

    const string FileName = "settings.json";

    // 設定のファイルのキー（言語で変えない。保存する名前はこの表だけで決める）
    internal const string KeySchemaVersion = "schemaVersion";
    internal const string KeyOutputFolder = "outputFolder";
    internal const string KeyFileName = "fileName";
    internal const string KeyFormats = "formats";
    internal const string KeyPngDpi = "pngDpi";
    static readonly string[] KnownKeys = { KeySchemaVersion, KeyOutputFolder, KeyFileName, KeyFormats, KeyPngDpi };

    /// <summary>利用者の設定のファイルの場所（既定は %APPDATA%\CADPeta\settings.json）。</summary>
    public static string DefaultPath()
    {
        string? dir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(dir))
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CADPeta");
        return Path.Combine(dir!, FileName);
    }

    public static SettingsLoadResult Load(string path, DateTime now)
    {
        var catalog = MessageCatalogs.Current;
        var defaults = CadPetaSettings.Default;
        if (!File.Exists(path))
            return new SettingsLoadResult(defaults, SettingsLoadStatus.Missing, Array.Empty<SettingsProblem>(), null, "");
        string text;
        try { text = File.ReadAllText(path, TextEncodings.Utf8NoBom); }
        catch (Exception ex) when (FileFailures.IsPathFailure(ex))
        {
            return Unrepairable(defaults, catalog.Format("settings.unreadable", path, FileFailures.OneLine(ex.Message)));
        }

        XElement root;
        try { root = ParseObject(text); }
        catch (Exception ex) when (IsUnparsable(ex))
        {
            return Recreate(path, now, FileFailures.OneLine(ex.Message));
        }

        var problems = new List<SettingsProblem>();
        int? version = ReadInt(root, KeySchemaVersion);
        if (version > CadPetaSettings.SchemaVersion)
            return new SettingsLoadResult(defaults, SettingsLoadStatus.NewerVersion,
                new[] { new SettingsProblem(KeySchemaVersion, catalog.Format("settings.newer_version", version, CadPetaSettings.SchemaVersion)) },
                null, catalog.Format("settings.newer_version_guide", path));
        if (version != CadPetaSettings.SchemaVersion)
            problems.Add(new SettingsProblem(KeySchemaVersion, catalog.Format("settings.version_missing", CadPetaSettings.SchemaVersion)));

        foreach (var unknown in Members(root).Select(m => m.Key).Where(k => !KnownKeys.Contains(k)).Distinct())
            problems.Add(new SettingsProblem(unknown, catalog.Format("settings.unknown_item")));
        // 同じ項目が 2 回以上あれば、どちらが正しいか分からないので、最初の値を使って直す
        foreach (var duplicate in Members(root).GroupBy(m => m.Key).Where(g => g.Count() > 1 && KnownKeys.Contains(g.Key)))
            problems.Add(new SettingsProblem(duplicate.Key, catalog.Format("settings.duplicate_item", duplicate.Count())));

        string folder = ReadValue(root, KeyOutputFolder, defaults.OutputFolder, problems, ReadString, v => CadPetaSettings.ValidateOutputFolder(v));
        string templateText = ReadValue(root, KeyFileName, defaults.FileName.Text, problems, ReadString, v => FileNameTemplate.Validate(v));
        var settings = new CadPetaSettings(
            folder,
            FileNameTemplate.Parse(templateText).Template!,
            ReadFormats(root, defaults.Formats, problems),
            ReadValue(root, KeyPngDpi, defaults.PngDpi, problems, ReadDouble, CadPetaSettings.ValidatePngDpi));

        if (problems.Count == 0)
            return new SettingsLoadResult(settings, SettingsLoadStatus.Ok, problems, null, "");

        // 直す前のファイルを残してから、直した設定を保存する
        return KeepAndSave(path, BackupName(path, now), settings, problems, SettingsLoadStatus.Repaired, "settings.repair_failed");
    }

    /// <summary>
    /// 設定を保存する。一時の名前で書き終えてから正式の名前に変える。失敗したら理由を返す（成功なら null）。
    /// 新しい版の CADPeta が書いた設定は、この版が知らない項目を消さないように上書きしない。
    /// </summary>
    public static string? Save(string path, CadPetaSettings settings)
    {
        if (WrittenVersion(path) is { } version && version > CadPetaSettings.SchemaVersion)
            return MessageCatalogs.Current.Format("settings.newer_version_not_saved", path, version, CadPetaSettings.SchemaVersion);
        string partial = path + ".partial";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(partial, JsonText.Serialize(SettingsJson.From(settings), indented: true), TextEncodings.Utf8NoBom);
            if (File.Exists(path)) File.Replace(partial, path, null);
            else File.Move(partial, path);
            return null;
        }
        catch (Exception ex) when (FileFailures.IsPathFailure(ex))
        {
            // 一時のファイルは正式の名前ではないので、次の読み込みを止めない。消せなければ次の保存で上書きする
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception cleanup) when (FileFailures.IsPathFailure(cleanup)) { }
            return FileFailures.OneLine(ex.Message);
        }
    }

    // 今のファイルの版。ファイルが無いか、読めないか、版が書かれていなければ null。読めないファイルは新しい版のものと判断できず、
    // 読み込みが既定で作り直すファイルと同じ扱いなので、上書きしてよい（書けなければ、書き込みの側の失敗として返る）
    static int? WrittenVersion(string path)
    {
        if (!File.Exists(path)) return null;
        try { return ReadInt(ParseObject(File.ReadAllText(path, TextEncodings.Utf8NoBom)), KeySchemaVersion); }
        catch (Exception ex) when (FileFailures.IsPathFailure(ex) || IsUnparsable(ex))
        {
            return null;
        }
    }

    // 設定のファイルを JSON の最上位のオブジェクトとして読めない（ParseObject が投げる失敗）
    static bool IsUnparsable(Exception ex) =>
        ex is XmlException or SerializationException or FormatException or InvalidDataException;

    static SettingsLoadResult Recreate(string path, DateTime now, string reason)
    {
        var defaults = CadPetaSettings.Default;
        var problems = new[] { new SettingsProblem("", MessageCatalogs.Current.Format("settings.not_json", reason)) };
        return KeepAndSave(path, BackupName(path, now), defaults, problems, SettingsLoadStatus.Recreated, "settings.recreate_failed");
    }

    // 今のファイルを kept の名前で写して残し、settings を保存する（直したときも、読めずに作り直したときも同じ .bak-<日時>）。
    // どちらかに失敗したら settings は使い、ファイルの場所と、CAD を終了して削除すれば作り直せることを返す
    static SettingsLoadResult KeepAndSave(string path, string kept, CadPetaSettings settings,
                                          IReadOnlyList<SettingsProblem> problems, SettingsLoadStatus done, string failureKey)
    {
        var catalog = MessageCatalogs.Current;
        try { File.Copy(path, kept); }
        catch (Exception ex) when (FileFailures.IsPathFailure(ex))
        {
            return new SettingsLoadResult(settings, SettingsLoadStatus.Unrepairable, problems, null, catalog.Format(failureKey, path, FileFailures.OneLine(ex.Message)));
        }
        string? saveError = Save(path, settings);
        return saveError == null
            ? new SettingsLoadResult(settings, done, problems, kept, "")
            : new SettingsLoadResult(settings, SettingsLoadStatus.Unrepairable, problems, kept, catalog.Format(failureKey, path, saveError));
    }

    static string BackupName(string path, DateTime now) =>
        FreeName(path + ".bak-" + now.ToString(FileNameTemplate.DateTimeFormat, CultureInfo.InvariantCulture));

    static SettingsLoadResult Unrepairable(CadPetaSettings defaults, string error) =>
        new(defaults, SettingsLoadStatus.Unrepairable, Array.Empty<SettingsProblem>(), null, error);

    // JSON を要素の木として読む（.NET 標準の JSON の読み取り）。最上位がオブジェクトでなければ読めないものとして扱う。
    // .NET の JSON の読み取りは、途中で切れたオブジェクト（書き込みの途中で止まったファイル）をファイルの終わりで閉じたものとして
    // 受け入れた（.NET Framework 4.8 と .NET 10、2026-10-28）ので、括弧が最後に閉じていることを先に確かめる
    static XElement ParseObject(string text)
    {
        if (!IsClosed(text))
            throw new InvalidDataException(MessageCatalogs.Current.Format("settings.truncated"));
        using var reader = JsonReaderWriterFactory.CreateJsonReader(TextEncodings.Utf8NoBom.GetBytes(text), XmlDictionaryReaderQuotas.Max);
        var root = XElement.Load(reader);
        if ((string?)root.Attribute("type") != "object")
            throw new InvalidDataException(MessageCatalogs.Current.Format("settings.not_object"));
        return root;
    }

    // 文字列の外の { [ と } ] の対応が、途中で負にならずに最後に 0 で閉じているか
    static bool IsClosed(string text)
    {
        int depth = 0;
        bool sawBracket = false, inString = false, escaped = false;
        foreach (char c in text)
        {
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': case '[': depth++; sawBracket = true; break;
                case '}': case ']': if (--depth < 0) return false; break;
            }
        }
        return sawBracket && depth == 0 && !inString;
    }

    // 最上位の項目（キーと要素）。XML の名前にできないキーは item 要素の item 属性に入る
    static IEnumerable<KeyValuePair<string, XElement>> Members(XElement root) =>
        root.Elements().Select(e => new KeyValuePair<string, XElement>((string?)e.Attribute("item") ?? e.Name.LocalName, e));

    static XElement? Member(XElement root, string key) =>
        Members(root).Where(m => m.Key == key).Select(m => m.Value).FirstOrDefault();

    // 項目を読む。無ければ既定を使い、問題にしない。型が違うか検証に通らなければ、既定を使って問題に加える
    static T ReadValue<T>(XElement root, string key, T fallback, List<SettingsProblem> problems,
                          Func<XElement, (bool Ok, T Value)> read, Func<T, string?> validate)
    {
        var element = Member(root, key);
        if (element == null) return fallback;
        var (ok, value) = read(element);
        if (!ok)
        {
            problems.Add(new SettingsProblem(key, MessageCatalogs.Current.Format("settings.wrong_type", element.Value)));
            return fallback;
        }
        string? error = validate(value);
        if (error != null)
        {
            problems.Add(new SettingsProblem(key, error));
            return fallback;
        }
        return value;
    }

    // 形式の一覧（名前の配列）。配列でなければ既定を使って問題に加える。知らない名前はその名前だけを除いて問題に加え、残りは使う
    static ArtifactKindSet ReadFormats(XElement root, ArtifactKindSet fallback, List<SettingsProblem> problems)
    {
        var element = Member(root, KeyFormats);
        if (element == null) return fallback;
        if ((string?)element.Attribute("type") != "array")
        {
            problems.Add(new SettingsProblem(KeyFormats, MessageCatalogs.Current.Format("settings.wrong_type", element.Value)));
            return fallback;
        }
        var formats = ArtifactKindSet.Empty;
        foreach (var item in element.Elements())
        {
            var kind = (string?)item.Attribute("type") == "string" ? ArtifactKinds.FromName(item.Value) : null;
            if (kind == null)
                problems.Add(new SettingsProblem(KeyFormats, MessageCatalogs.Current.Format("settings.unknown_format", item.Value,
                    string.Join(" ", ArtifactKinds.All.Select(ArtifactKinds.Name)))));
            else
                formats = formats.With(kind.Value, true);
        }
        return formats;
    }

    static (bool, string) ReadString(XElement e) => ((string?)e.Attribute("type") == "string" ? (true, e.Value) : (false, ""));

    static (bool, double) ReadDouble(XElement e) =>
        (string?)e.Attribute("type") == "number" && double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? (true, d) : (false, 0);

    static int? ReadInt(XElement root, string key)
    {
        var e = Member(root, key);
        return e != null && (string?)e.Attribute("type") == "number" && int.TryParse(e.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : null;
    }

    static string FreeName(string path)
    {
        string candidate = path;
        for (int n = 2; File.Exists(candidate); n++)
            candidate = path + "_" + n.ToString(CultureInfo.InvariantCulture);
        return candidate;
    }

    // 設定のファイルの形。キーと順番は上の表と同じ
    [DataContract]
    sealed class SettingsJson
    {
        [DataMember(Name = KeySchemaVersion, Order = 1)] public int SchemaVersion { get; set; }
        [DataMember(Name = KeyOutputFolder, Order = 2)] public string OutputFolder { get; set; } = "";
        [DataMember(Name = KeyFileName, Order = 3)] public string FileName { get; set; } = "";
        [DataMember(Name = KeyFormats, Order = 4)] public List<string> Formats { get; set; } = new();
        [DataMember(Name = KeyPngDpi, Order = 5)] public double PngDpi { get; set; }

        public static SettingsJson From(CadPetaSettings s) => new()
        {
            SchemaVersion = CadPetaSettings.SchemaVersion,
            OutputFolder = s.OutputFolder,
            FileName = s.FileName.Text,
            Formats = s.Formats.Kinds.Select(ArtifactKinds.Name).ToList(),
            PngDpi = s.PngDpi,
        };
    }
}
