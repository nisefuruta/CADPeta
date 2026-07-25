#if CADPETA_ACAD
using Autodesk.AutoCAD.DatabaseServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.DatabaseServices;
using CadApplication = Teigha.ApplicationServices.Application;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.DatabaseServices;
using CadApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.DatabaseServices;
using CadApplication = Bricscad.ApplicationServices.Application;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using System.Globalization;

/// <summary>
/// ホスト CAD の表示言語ヒント（4製品共通）。言語カタログの選択（Msg.Initialize）へ渡す
/// 判定材料だけを集め、どのカタログを使うかの判定は Msg.ChooseLcid（言語ファミリ写像・
/// 全滅時 en）に一本化する。
/// </summary>
internal static class CadHostLanguage
{
    /// <summary>表示言語の明示指定（"ja"/"en"）。ホスト CAD の言語と独立に文言言語を
    /// 切り替える口で、未設定ならホスト言語の自動判定に従う。</summary>
    const string LanguageEnvVar = "CADPETA_LANG";

    /// <summary>
    /// 判定材料を確度の高い順に集める: (1) 環境変数 CADPETA_LANG（明示指定）、
    /// (2) システム変数 LOCALE（AutoCAD 系の言語コード）、
    /// (3) 製品登録レジストリキーのパス（AutoCAD 系は末尾に ":LCIDの16進" を含む）、
    /// (4) プロセスの CurrentUICulture。取れない材料は null で埋める。
    /// </summary>
    public static IReadOnlyList<string?> Hints() => new[]
    {
        Probe(() => Environment.GetEnvironmentVariable(LanguageEnvVar)),
        Probe(() => CadApplication.GetSystemVariable("LOCALE") as string),
#if CADPETA_ARES
        // ARES 2020 の SDK（TD_Mgd_4.03_14）には UserRegistryProductRootKey が無い。
        // RegistryProductRootKey は対応全世代（2020/2023/2024/2027）の SDK にあるためこちらを使う
        Probe(() => HostApplicationServices.Current.RegistryProductRootKey),
#else
        Probe(() => HostApplicationServices.Current.UserRegistryProductRootKey),
#endif
        CultureInfo.CurrentUICulture.Name,
    };

    static string? Probe(Func<string?> getter)
    {
        try { return getter(); }
        catch (System.Exception)
        {
            // 取得失敗は「このヒントが無い」ことだけを意味する。後続ヒントと en 既定で必ず
            // 決まり、全ヒントが無いときは Msg.Initialize が判定不能をログへ報告する
            return null;
        }
    }
}
