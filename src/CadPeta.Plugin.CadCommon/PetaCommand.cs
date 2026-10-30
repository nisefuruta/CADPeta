using System;
using System.IO;

#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
[assembly: CommandClass(typeof(CadPeta.Plugin.Acad.PetaCommand))]
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.Runtime;
using CadApplication = Teigha.ApplicationServices.Application;
using CadRuntimeException = Teigha.Runtime.Exception;
[assembly: CommandClass(typeof(CadPeta.Plugin.Ares.PetaCommand))]
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.Runtime;
using CadApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
using CadRuntimeException = Itjp.Ijcad.Runtime.Exception;
[assembly: CommandClass(typeof(CadPeta.Plugin.Ijcad.PetaCommand))]
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.Runtime;
using CadApplication = Bricscad.ApplicationServices.Application;
using CadRuntimeException = Teigha.Runtime.Exception;
[assembly: CommandClass(typeof(CadPeta.Plugin.Bricscad.PetaCommand))]
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Acquire.Pdf;
using CadPeta.Out.Emf;
using CadPeta.Plugin.Shared;

// CAD の薄い殻（4 製品で共有）。PETA/PETADIAGはPDF印刷単一経路のPetaRunnerへ、PETADATA/PETADATADIAGは
// DB直接取得のSvgExportRunnerへ、それぞれCAD固有ポートだけを結線する。PETADATA系の結線は
// CadDataExportCommands.cs（このクラスの partial）が持つ。製品ごとの違い（SDK の名前空間、
// 例外の型と説明文）は #if で分ける。

public static partial class PetaCommand
{
    // UsePickSet: コマンド起動時の選択（先選択）を SelectImplied で引き継ぐ（COPYCLIP と同じ作法）
    [CommandMethod(PluginDefaults.CommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void Execute() => Invoke(diagnostic: false);

    // 診断モード。PETA と同一処理に、変換層の各段の前後 SVG ダンプを加える。
    // CommandClass 属性を1つでも付けたアセンブリは列挙したクラスしかコマンド登録されないが、
    // これは既存の PetaCommand クラス内への CommandMethod 追加のため、属性の追加は不要。
    [CommandMethod(PluginDefaults.DiagCommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void ExecuteDiag() => Invoke(diagnostic: true);

    // PETADATA/PETADATADIAG（構造SVG書き出し）は CadDataExportCommands.cs（partial）が結線する。

    static void Invoke(bool diagnostic) =>
        RunAtCommandBoundary(diagnostic ? PluginDefaults.DiagCommandName : PluginDefaults.CommandName, (doc, log) =>
            PetaRunner.Run(BuildEnvironment(doc, log, diagnostic ? log : TextWriter.Null), log, diagnostic));

    /// <summary>
    /// コマンドの境界（PETA 系と PETADATA 系で共通）。アクティブな図面のコマンドラインへ報告する書き込み先を作り、
    /// 言語を決めてから <paramref name="body"/> を実行する。例外はここで止めて報告する。
    /// </summary>
    static void RunAtCommandBoundary(string commandName, Action<Document, EditorTextWriter> body)
    {
        // アクティブ図面がない状態ではエディタ（報告先）自体が存在しない。
        // 本コマンドは図面文脈でしか起動されないため、防御的な無処理復帰のみ行う
        var doc = CadApplication.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        var log = new EditorTextWriter(doc.Editor);
        // 言語スコープは catch のエラー説明まで維持して finally で解除する（using を try 内に
        // 置くと catch より先に破棄され、エラー表示だけ既定言語へ戻る）。初期化自体の例外も
        // 境界の内側で受け、ホストへ抜けさせない
        IDisposable? languageScope = null;
        try
        {
            using var promptTrace = PromptTrace.BeginCommand(commandName);
            languageScope = Msg.Initialize(CadHostLanguage.Hints(), log);
            body(doc, log);
        }
        catch (System.Exception ex)
        {
            // ホスト CAD を巻き込まないため例外はコマンド境界で止める。握りつぶさない:
            // 種別ごとの対処案内に加え、内部例外の連鎖とスタックを全量表示する
            // （実測: net48 の版束縛失敗は InnerException まで見ないと原因が特定できない）
            log.WriteLine(Msg.Format("error.prefix", Describe(ex)));
            log.WriteLine(ex.ToString());
        }
        finally
        {
            languageScope?.Dispose();
            log.Flush();
        }
    }

    // 合成ルート: CAD 固有のポート・取得・出力を束ねて本体フローへ渡す環境を作る。
    static PetaEnvironment BuildEnvironment(Document doc, TextWriter log, TextWriter verbose)
    {
        return new PetaEnvironment
        {
            Editor = new CadEditorPort(doc),
            Plotter = new CadPlotPort(doc),
            SelectionPlotter = new CadSelectionPlotPort(doc),
            PdfAcquirer = new PdfIrAcquirer(log, verbose),
            EmfWriterFactory = background => new EmfWriter(background, log, verbose),
            SetClipboardFromFile = path => EmfClipboard.SetFromFile(path, log, verbose),
            TrueTypeOutlinerFactory = () => new GdiGlyphOutliner(),
        };
    }

    /// <summary>利用者が次の行動を判断できる、例外種別ごとの説明文。枠は言語カタログから引き、
    /// 内挿する ex.Message・ErrorStatus は下位層・BCL の原文のまま残す。</summary>
    static string Describe(System.Exception ex) => ex switch
    {
        // PlotOrchestrator / OutputLocation が文脈込みで組み立てた説明はそのまま使う
        InvalidOperationException => ex.Message,
#if CADPETA_ACAD
        CadRuntimeException rex => Msg.Format("error.api_failed_acad", rex.ErrorStatus),
#else
        CadRuntimeException rex => Msg.Format("error.api_failed", ProductName, rex.ErrorStatus),
#endif
        UnauthorizedAccessException or System.Security.SecurityException =>
            Msg.Format("error.access_denied", ex.Message),
        IOException => Msg.Format("error.io_failed", ex.Message),
#if CADPETA_ACAD
        NotSupportedException => Msg.Format("error.unsupported_acad", ex.Message),
#else
        NotSupportedException => Msg.Format("error.unsupported", ex.Message),
#endif
        _ => ex.Message,
    };

#if CADPETA_ARES
    const string ProductName = "ARES";
#elif CADPETA_IJCAD
    const string ProductName = "IJCAD";
#elif CADPETA_BRICSCAD
    const string ProductName = "BricsCAD";
#endif
}
