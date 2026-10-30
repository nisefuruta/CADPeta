using System;
using System.Reflection;
using System.Runtime.Versioning;

namespace CadPeta.UseCases;

/// <summary>
/// 「どのビルドが動いているか」を GUI に1行で出すための不変ビルド識別子。
/// 更新日時は使わない（コピー・展開で変わり得るため）。DLL に既に埋め込まれている
/// アセンブリ属性（版数+SHA・ビルド構成・TFM）を読むだけで、ビルド時の追加作業を要さない。
/// </summary>
public static class BuildStamp
{
    /// <summary>
    /// 純粋な整形関数。テスト容易性のため、アセンブリ読み取り（<see cref="Describe"/>）と分離する。
    /// どの入力（null・空・想定外の形式）でも例外を投げず、フォールバック文字列を返す。
    /// </summary>
    /// <param name="informationalVersion">
    /// "base+sha" 形式（AssemblyInformationalVersionAttribute 由来）。SHA は先頭8文字へ短縮する
    /// （完全SHAは長すぎて1行表示に不向き）。'+' が無ければそのまま使う。
    /// </param>
    /// <param name="configuration">ビルド構成（AssemblyConfigurationAttribute 由来。"Debug"/"Release"）。</param>
    /// <param name="frameworkName">ターゲットTFM（TargetFrameworkAttribute 由来）。</param>
    /// <param name="location">読み込み元 DLL の絶対パス。<paramref name="includePath"/>=true のときのみ使う。</param>
    /// <param name="includePath">
    /// true のときだけ「読み込み元: {location}」を追記する。絶対パスは環境情報を含むため、
    /// 通常表示（PETA）では出さず診断表示（PETADIAG）のときだけ付ける。
    /// </param>
    public static string Format(string? informationalVersion, string? configuration,
                                string? frameworkName, string? location, bool includePath)
    {
        // 表示文言は実行コンテキストの言語カタログから引く（未初期化の文脈は ja）
        var msgs = MessageCatalogs.Current;
        string version = ShortenVersion(informationalVersion);
        string config = string.IsNullOrEmpty(configuration)
            ? msgs.Format("stamp.unknown_config") : configuration!;
        string tfm = string.IsNullOrEmpty(frameworkName)
            ? msgs.Format("stamp.unknown_tfm") : frameworkName!;

        string line = $"{version} / {config} / {tfm}";
        if (!includePath)
            return line; // 絶対パスは出さない（通常表示）

        string path = string.IsNullOrEmpty(location) ? msgs.Format("stamp.unknown_path") : location!;
        return line + Environment.NewLine + msgs.Format("stamp.loaded_from", path);
    }

    /// <summary>"base+sha" の sha 部分を先頭8文字へ短縮する。'+' が無い・空なら素通しする。</summary>
    static string ShortenVersion(string? informationalVersion)
    {
        if (string.IsNullOrEmpty(informationalVersion))
            return MessageCatalogs.Current.Format("stamp.unknown_version");
        int plus = informationalVersion!.IndexOf('+');
        if (plus < 0)
            return informationalVersion;
        string baseVersion = informationalVersion.Substring(0, plus);
        string sha = informationalVersion.Substring(plus + 1);
        string shortSha = sha.Length > 8 ? sha.Substring(0, 8) : sha;
        return $"{baseVersion}+{shortSha}";
    }

    /// <summary>
    /// アセンブリから版数・構成・TFM・読み込み元パスを読み、<see cref="Format"/> へ渡す。
    /// InformationalVersion 属性が無ければアセンブリ版数（AssemblyName.Version）で代替する。
    /// </summary>
    public static string Describe(Assembly asm, bool includePath)
    {
        string? informationalVersion =
            asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? asm.GetName().Version?.ToString();
        string? configuration = asm.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        string? frameworkName = asm.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;
        return Format(informationalVersion, configuration, frameworkName, asm.Location, includePath);
    }
}
