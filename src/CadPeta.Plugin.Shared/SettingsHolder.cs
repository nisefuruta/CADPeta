using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>今の設定と、今回ファイルを読み直したときだけ表示する文（読み直さなかったときは空）。</summary>
public sealed record SettingsSnapshot(CadPetaSettings Settings, IReadOnlyList<string> Messages);

/// <summary>
/// プラグインが使う設定を 1 つ持つ（CAD の 1 つのプロセスに 1 つ。コマンドの組み立てが作る）。最初の取得でファイルを読み、
/// ファイルが変わるまで読み直さない。利用者がファイルを直接直したときは、次のコマンドで読み直す。
/// 直したことなどの表示は読み直したときだけ返し、PETA のたびに同じ表示を繰り返さない。
/// </summary>
public sealed class SettingsHolder
{
    readonly string _path;
    CadPetaSettings? _settings;
    FileStamp _stamp;

    public SettingsHolder(string path) => _path = path;

    /// <summary>設定のファイルの場所。</summary>
    public string SettingsPath => _path;

    public SettingsSnapshot Get()
    {
        if (_settings != null && FileStamp.Of(_path) == _stamp)
            return new SettingsSnapshot(_settings, Array.Empty<string>());
        var result = CadPetaSettingsStore.Load(_path, DateTime.Now);
        _settings = result.Settings;
        // 読み込みが直した設定を保存したときは、保存した後のファイルを基準にする
        _stamp = FileStamp.Of(_path);
        return new SettingsSnapshot(result.Settings, result.Messages());
    }

    /// <summary>設定を保存し、持っている設定も置き換える。保存できなければ理由を返し、前の設定を使い続ける（成功なら null）。</summary>
    public string? Save(CadPetaSettings settings)
    {
        string? error = CadPetaSettingsStore.Save(_path, settings);
        if (error != null)
            return error;
        _settings = settings;
        _stamp = FileStamp.Of(_path);
        return null;
    }

    // ファイルが変わったかを、有無・更新日時・大きさで判断する
    readonly record struct FileStamp(bool Exists, DateTime LastWriteUtc, long Length)
    {
        public static FileStamp Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
        }
    }
}
