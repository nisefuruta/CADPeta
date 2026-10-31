#if CADPETA_ACAD
using Autodesk.AutoCAD.Runtime;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.Runtime;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.Runtime;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Teigha.Runtime;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using CadPeta.Plugin.Shared;

// PETAOPT（設定のダイアログ）と -PETAOPT（設定のコマンドライン版）。設定は PETA と同じ PetaCommand.Settings を使う。
// どちらも図面を変えないので NoUndoMarker を付ける（直後の UNDO が PETAOPT を戻すだけにならないように）。
public static partial class PetaCommand
{
#if CADPETA_ACAD
    // 画面のある AutoCAD か（accoreconsole にはダイアログを開く画面とリボンが無い）。プロセスの名前で 1 回だけ調べる
    static readonly Lazy<bool> Screen = new(() =>
        !string.Equals(Process.GetCurrentProcess().ProcessName, "accoreconsole", StringComparison.OrdinalIgnoreCase));
#else
    // ほかの CAD は画面の無いホストで読み込まない
    static readonly Lazy<bool> Screen = new(() => true);
#endif

    /// <summary>ダイアログとリボンを使える（画面のある）CAD か。</summary>
    internal static bool HasScreen => Screen.Value;

    [CommandMethod(PluginDefaults.OptCommandName, CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void ExecuteOpt() =>
        RunAtCommandBoundary(PluginDefaults.OptCommandName, (doc, log) =>
        {
            if (!HasScreen)
            {
                log.WriteLine(Msg.Format("opt.no_screen", PluginDefaults.OptCommandLineName));
                return;
            }
            var snapshot = Settings.Get();
            foreach (string message in snapshot.Messages)
                log.WriteLine(message);
            var ed = new CadEditorPort(doc);
            bool saved = OpenSettingsDialog(snapshot.Settings, ed.DrawingStem, ed.CurrentLayoutName);
            log.WriteLine(saved ? Msg.Format("opt.saved", Settings.SettingsPath) : Msg.Text("opt.unchanged"));
        });

    // 画面の型を読み込むのはこのメソッドを JIT するときだけ（HasScreen を確かめてから呼ぶ）
    [MethodImpl(MethodImplOptions.NoInlining)]
    static bool OpenSettingsDialog(CadPeta.UseCases.CadPetaSettings settings, string drawing, string layout) =>
        GuiCommands.ShowSettingsDialog(Settings, settings, drawing, layout);

    [CommandMethod(PluginDefaults.OptCommandLineName, CommandFlags.Modal | CommandFlags.NoUndoMarker)]
    public static void ExecuteOptCommandLine() =>
        RunAtCommandBoundary(PluginDefaults.OptCommandLineName, (doc, log) =>
            SettingsCommandLine.Run(new CadEditorPort(doc), log, Settings));
}