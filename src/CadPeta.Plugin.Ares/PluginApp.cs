// NETLOAD / 自動読込の直後に一度だけ呼ばれる読込フック。どのビルドを読み込んだかを
// コマンドライン1行（＋読込元パス）で示す。以後は PETA/PETADIAG 実行ごとには出さない
// （毎回表示は冗長という利用者判断）。版数表示の失敗で読込自体を止めないよう例外は飲む。
using Teigha.ApplicationServices;
using Teigha.Runtime;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;

[assembly: ExtensionApplication(typeof(CadPeta.Plugin.Ares.PluginApp))]

namespace CadPeta.Plugin.Ares;

public sealed class PluginApp : IExtensionApplication
{
    public void Initialize()
    {
        try
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
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
        catch (System.Exception ex)
        {
            System.Diagnostics.Trace.WriteLine("[CadPeta] 読込時の版数表示に失敗した: " + ex);
        }
    }

    public void Terminate() { }
}
