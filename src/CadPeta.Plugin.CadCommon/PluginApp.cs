// NETLOAD / 自動読込の直後に一度だけ呼ばれる読込フック。どのビルドを読み込んだかを
// コマンドライン1行（＋読込元パス）で示す。以後は PETA/PETADIAG 実行ごとには出さない
// （毎回表示は冗長という利用者判断）。読み込み自体は止めない。読み込みの記録と版の表示の失敗は、
// デバッグトレースへ記録して続ける（PluginStartup）。
// 4 製品で同じ実装なので、SDK の名前空間だけを #if で分けて共有する。
#if CADPETA_ACAD
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
[assembly: ExtensionApplication(typeof(CadPeta.Plugin.Acad.PluginApp))]
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.Runtime;
using CadApplication = Teigha.ApplicationServices.Application;
[assembly: ExtensionApplication(typeof(CadPeta.Plugin.Ares.PluginApp))]
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.Runtime;
using CadApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
[assembly: ExtensionApplication(typeof(CadPeta.Plugin.Ijcad.PluginApp))]
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.Runtime;
using CadApplication = Bricscad.ApplicationServices.Application;
[assembly: ExtensionApplication(typeof(CadPeta.Plugin.Bricscad.PluginApp))]
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;

public sealed class PluginApp : IExtensionApplication
{
    public void Initialize()
    {
        // どちらも失敗してもコマンドは正しく動くので、記録して読み込みを続ける
        // 試験のときだけ、読み込んだアセンブリの場所を記録する。インストール済みのプラグインが
        // 自動で読み込まれていると、同じ名前のアセンブリは後から読み込めず、試験が別の DLL を試験してしまう
        PluginStartup.Run("Plugin.Load", () => PromptTrace.Write("Plugin.Load", "Loaded",
            typeof(PluginApp).Assembly.Location, valueIsNull: false, count: null), ReportOptionalFailure);
        PluginStartup.Run("Version", ShowLoadedBuild, ReportOptionalFailure);
    }

    static void ShowLoadedBuild()
    {
        var doc = CadApplication.DocumentManager.MdiActiveDocument;
        if (doc == null) return;
        // コマンドスコープの外の一度きりの表示。カタログをここで解決し、
        // BuildStamp（ambient 参照）へは言語スコープで伝える
        var catalog = Msg.ResolveCatalog(CadHostLanguage.Hints());
        using (MessageCatalogs.BeginLanguageScope(catalog))
        using (MessageCatalog.BeginFormatFallbackScope((catalogName, key, error) =>
            doc.Editor.WriteMessage("\n" + catalog.Format("lang.format_fallback", catalogName, key, error))))
            doc.Editor.WriteMessage("\n" + catalog.Format("app.loaded",
                PluginDefaults.CommandName,
                BuildStamp.Describe(typeof(PluginApp).Assembly, includePath: true)) + "\n");
    }

    static void ReportOptionalFailure(string step, System.Exception ex) =>
        System.Diagnostics.Trace.WriteLine($"[CadPeta] 読み込み時の {step} に失敗した: {ex}");

    public void Terminate() { }
}
