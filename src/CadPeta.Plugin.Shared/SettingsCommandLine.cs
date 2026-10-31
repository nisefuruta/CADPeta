using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CadPeta.Geometry;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// -PETAOPT（設定のコマンドライン版。accoreconsole と自動の試験でも使える）。今の設定を表示し、項目のキーワードで値を変え、
/// Enter か終了(X)で保存する。使えない値は理由を出して元の値のまま、Esc は変更を破棄して保存しない。変更が無ければ保存しない。
/// キーワードの表はこのクラスだけが持つ。
/// </summary>
public static class SettingsCommandLine
{
    public const string KeywordFormats = "Formats";
    public const string KeywordFolder = "Output";
    public const string KeywordName = "Name";
    public const string KeywordDpi = "Dpi";
    public const string KeywordExit = "eXit";

    /// <summary>項目を選ぶキーワード。</summary>
    public static IReadOnlyList<PromptKeyword> MainKeywords => new[]
    {
        new PromptKeyword(KeywordFormats, "F", Msg.Text("opt.keyword.formats")),
        new PromptKeyword(KeywordFolder, "O", Msg.Text("opt.keyword.folder")),
        new PromptKeyword(KeywordName, "N", Msg.Text("opt.keyword.name")),
        new PromptKeyword(KeywordDpi, "D", Msg.Text("opt.keyword.dpi")),
        new PromptKeyword(KeywordExit, "X", Msg.Text("opt.keyword.exit")),
    };

    public static void Run(IEditorPort ed, TextWriter log, SettingsHolder holder)
    {
        var snapshot = holder.Get();
        foreach (string message in snapshot.Messages)
            log.WriteLine(message);
        var original = snapshot.Settings;
        var settings = original;
        var keywords = MainKeywords;
        string prompt = Msg.Format("opt.prompt", string.Join("/", keywords.Select(k => k.Display)), keywords[keywords.Count - 1].Display);

        while (true)
        {
            Describe(ed, settings, holder.SettingsPath);
            string? choice = PromptChoice(ed, prompt, keywords);
            if (choice == null)
            {
                log.WriteLine(Msg.Text("opt.discarded"));
                return;
            }
            if (choice == KeywordExit)
            {
                Save(log, holder, original, settings);
                return;
            }
            CadPetaSettings? changed = choice switch
            {
                KeywordFormats => EditFormats(ed, settings),
                KeywordFolder => EditText(ed, Msg.Format("opt.folder_prompt", settings.OutputFolder),
                    value => CadPetaSettings.ValidateOutputFolder(value), value => settings with { OutputFolder = value }, settings),
                KeywordName => EditText(ed, Msg.Format("opt.name_prompt", FileNameTemplate.TokenList, settings.FileName.Text),
                    value => FileNameTemplate.Validate(value),
                    value => settings with { FileName = FileNameTemplate.Parse(value).Template! }, settings),
                KeywordDpi => EditDpi(ed, settings),
                _ => settings,
            };
            if (changed == null)
            {
                log.WriteLine(Msg.Text("opt.discarded"));
                return;
            }
            settings = changed;
        }
    }

    static void Save(TextWriter log, SettingsHolder holder, CadPetaSettings original, CadPetaSettings settings)
    {
        if (settings == original)
        {
            log.WriteLine(Msg.Text("opt.unchanged"));
            return;
        }
        string? error = holder.Save(settings);
        log.WriteLine(error == null
            ? Msg.Format("opt.saved", holder.SettingsPath)
            : Msg.Format("opt.save_failed", holder.SettingsPath, error));
    }

    // 形式は -OSNAP と同じく、オンにする形式の一覧をカンマ区切りで入力する（NONE は 0 件）。Enter は元の値のまま、
    // 使えない一覧は理由を出して元の値のまま。Esc は null
    static CadPetaSettings? EditFormats(IEditorPort ed, CadPetaSettings settings) =>
        EditText(ed, Msg.Format("opt.formats_prompt", string.Join(" ", ArtifactKinds.All.Select(k => ArtifactKinds.Label(k) + "=" + ArtifactKindSet.Of(k).Value.ToString(System.Globalization.CultureInfo.InvariantCulture))), ArtifactKinds.NoneKeyword, FormatInput(settings.Formats)),
            value => ArtifactKinds.ParseList(value).Error,
            value => settings with { Formats = ArtifactKinds.ParseList(value).Set!.Value }, settings);

    // 今の形式を、そのまま入力し直せる名前の一覧（PDF,EMF または NONE）と値の合計で表す（PDF,EMF (5)）
    static string FormatInput(ArtifactKindSet formats) =>
        (formats.IsEmpty ? ArtifactKinds.NoneKeyword : string.Join(",", formats.Kinds.Select(ArtifactKinds.Label)))
        + " (" + formats.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";
    // キーワードの入力。Esc は null、Enter は終了(X)と同じ、それ以外は選んだキーワードのグローバル名
    static string? PromptChoice(IEditorPort ed, string prompt, IReadOnlyList<PromptKeyword> keywords)
    {
        var r = ed.PromptKeywords(prompt, keywords);
        return r.Outcome switch
        {
            PromptOutcome.Cancelled => null,
            PromptOutcome.None => KeywordExit,
            _ => r.Keyword,
        };
    }

    // 文字列の項目。Enter は元の値のまま、使えない値は理由を出して元の値のまま。Esc は null
    static CadPetaSettings? EditText(IEditorPort ed, string prompt, Func<string, string?> validate,
                                     Func<string, CadPetaSettings> apply, CadPetaSettings settings)
    {
        string? answer = ed.PromptString(prompt);
        if (answer == null)
            return null;
        string value = answer.Trim();
        if (value.Length == 0)
            return settings;
        if (validate(value) is { } error)
        {
            ed.WriteLine(error);
            return settings;
        }
        return apply(value);
    }

    static CadPetaSettings? EditDpi(IEditorPort ed, CadPetaSettings settings)
    {
        var r = ed.PromptDouble(Msg.Format("opt.dpi_prompt", RasterLimits.MinDpi, CadPetaSettings.MaxPngDpi, settings.PngDpi), Array.Empty<PromptKeyword>(),
            settings.PngDpi, useDefault: true, allowNone: true, allowZero: false, allowNegative: false);
        if (r.Outcome == PromptOutcome.Cancelled)
            return null;
        if (r.Outcome != PromptOutcome.Value)
            return settings;
        if (CadPetaSettings.ValidatePngDpi(r.Value) is { } error)
        {
            ed.WriteLine(error);
            return settings;
        }
        return settings with { PngDpi = r.Value };
    }

    static void Describe(IEditorPort ed, CadPetaSettings settings, string path)
    {
        ed.WriteLine(Msg.Format("opt.current.formats", FormatInput(settings.Formats)));
        ed.WriteLine(Msg.Format("opt.current.folder", settings.OutputFolder));
        ed.WriteLine(Msg.Format("opt.current.name", settings.FileName.Text));
        ed.WriteLine(Msg.Format("opt.current.dpi", settings.PngDpi));
        ed.WriteLine(Msg.Format("opt.current.file", path));
    }

}