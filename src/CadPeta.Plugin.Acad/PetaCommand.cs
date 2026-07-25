// AutoCADの薄い殻。PETA/PETADIAGはPDF印刷単一経路のPetaRunnerへ、PETADATA/PETADATADIAGは
// DB直接取得のSvgExportRunnerへ、それぞれCAD固有ポートだけを結線する。PETADATA系の結線は
// CadPeta.Plugin.CadCommon\CadDataExportCommands.cs（このクラスの partial）へ4製品共通で1本化した。
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.ApplicationServices.Core;
using Autodesk.AutoCAD.Runtime;
using CadPeta.Acquire.Pdf;
using CadPeta.Out.Emf;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;

[assembly: CommandClass(typeof(CadPeta.Plugin.Acad.PetaCommand))]

namespace CadPeta.Plugin.Acad;

public static partial class PetaCommand
{
    static PetaCommand()
    {
        // net48 ホストは厳密な版束縛のため、依存パッケージ間の参照版差（実測: PdfPig →
        // System.Memory 4.0.1.2 要求に対し System.Text.Json 9 由来の 4.0.2.0 が配備され
        // FileLoadException）が起きる。クラスライブラリは bindingRedirect を持てないため、
        // プラグインフォルダの同名アセンブリへ後段解決する（net8/net10 では発火しないだけで無害）
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            try
            {
                var name = new System.Reflection.AssemblyName(e.Name).Name;
                var dir = Path.GetDirectoryName(typeof(PetaCommand).Assembly.Location);
                if (name == null || dir == null)
                    return null;
                var candidate = Path.Combine(dir, name + ".dll");
                return File.Exists(candidate) ? System.Reflection.Assembly.LoadFrom(candidate) : null;
            }
            catch (System.Exception ex) when (ex is IOException or FileLoadException
                or BadImageFormatException or UnauthorizedAccessException
                or System.Security.SecurityException or ArgumentException)
            {
                // ここで候補を返せなくても握りつぶしにはならない: null を返すと元の束縛失敗が
                // FileNotFoundException / FileLoadException としてそのまま表面化し、
                // コマンド境界の報告（内部例外連鎖込み）に乗る
                return null;
            }
        };
    }

    // UsePickSet: コマンド起動時の選択（先選択）を SelectImplied で引き継ぐ（COPYCLIP と同じ作法）
    [CommandMethod(PluginDefaults.CommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void Execute() => Invoke(diagnostic: false);

    // 診断モード。PETA と同一処理に、変換層の各段の前後 SVG ダンプを加える。
    // CommandClass 属性を1つでも付けたアセンブリは列挙したクラスしかコマンド登録されないが、
    // これは既存の PetaCommand クラス内への CommandMethod 追加のため、属性の追加は不要。
    [CommandMethod(PluginDefaults.DiagCommandName, CommandFlags.Modal | CommandFlags.UsePickSet)]
    public static void ExecuteDiag() => Invoke(diagnostic: true);

    // PETADATA/PETADATADIAG（構造SVG書き出し）は CadDataExportCommands.cs（partial）が結線する。

    static void Invoke(bool diagnostic)
    {
        // アクティブ図面がない状態ではエディタ（報告先）自体が存在しない。
        // 本コマンドは図面文脈でしか起動されないため、防御的な無処理復帰のみ行う
        var doc = Application.DocumentManager.MdiActiveDocument;
        if (doc == null)
            return;
        var log = new EditorTextWriter(doc.Editor);
        TextWriter verbose = diagnostic ? log : TextWriter.Null;
        // 言語スコープは catch のエラー説明まで維持して finally で解除する（using を try 内に
        // 置くと catch より先に破棄され、エラー表示だけ既定言語へ戻る）。初期化自体の例外も
        // 境界の内側で受け、ホストへ抜けさせない
        IDisposable? languageScope = null;
        try
        {
            using var promptTrace = PromptTrace.BeginCommand(
                diagnostic ? PluginDefaults.DiagCommandName : PluginDefaults.CommandName);
            languageScope = Msg.Initialize(CadHostLanguage.Hints(), log);
            var env = BuildEnvironment(doc, log, verbose);
            PetaRunner.Run(env, log, diagnostic);
        }
        catch (System.Exception ex)
        {
            // ホスト AutoCAD を巻き込まないため例外はコマンド境界で止める。握りつぶさない:
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
            Editor = new AcadEditorPort(doc),
            Plotter = new AcadPlotPort(doc),
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
        Autodesk.AutoCAD.Runtime.Exception rex => Msg.Format("error.api_failed_acad", rex.ErrorStatus),
        UnauthorizedAccessException or System.Security.SecurityException =>
            Msg.Format("error.access_denied", ex.Message),
        IOException => Msg.Format("error.io_failed", ex.Message),
        NotSupportedException => Msg.Format("error.unsupported_acad", ex.Message),
        _ => ex.Message,
    };
}
