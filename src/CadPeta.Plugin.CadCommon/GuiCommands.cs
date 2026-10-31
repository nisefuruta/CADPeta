using System.Runtime.CompilerServices;
using CadPeta.Plugin.Shared;
using CadPeta.Ui.Wpf;
using CadPeta.UseCases;

#if CADPETA_ACAD
using System;
using System.Globalization;
using Autodesk.AutoCAD.ApplicationServices;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif

/// <summary>
/// 画面のある CAD でだけ動くコード（WPF のダイアログ、AutoCAD の acmgd とリボン）を閉じ込める。AutoCAD の accoreconsole には
/// acmgd・AdWindows が無いので、呼び出し側は <see cref="PetaCommand.HasScreen"/> を確かめてから呼ぶ。メソッドはインライン化させない
/// （accoreconsole で呼び出し側を JIT するとき、このクラスの参照する型を読み込ませない）。引数と戻り値に画面の型を使わない。
/// </summary>
internal static partial class GuiCommands
{
    /// <summary>設定のダイアログをモーダルで開き、保存したかを返す。保存は <paramref name="holder"/> が行う。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ShowSettingsDialog(SettingsHolder holder, CadPetaSettings settings, string drawing, string layout)
    {
#if CADPETA_ACAD
        var dialog = new SettingsDialog(settings, holder.SettingsPath, drawing, layout, IsDarkTheme(), holder.Save);
        Application.ShowModalWindow(dialog);
#else
        // ShowModalWindow の無い CAD は、CAD のメインのウィンドウを所有者にして開く
        var dialog = new SettingsDialog(settings, holder.SettingsPath, drawing, layout, dark: false, holder.Save);
        dialog.ShowDialogOwnedBy(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
#endif
        // ダイアログのイベントの処理で想定外の例外が起きていたら、ここから投げ直す（コマンドの最外が報告する）
        dialog.ThrowIfFailed();
        return dialog.Saved;
    }

    /// <summary>ファイル名のコメントのダイアログをモーダルで開き、OK なら true（入力したコメントは <paramref name="form"/> に入る）を返す。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool ShowCommentDialog(CommentForm form)
    {
#if CADPETA_ACAD
        var dialog = new CommentDialog(form, IsDarkTheme());
        // ダイアログはいつも AutoCAD のウィンドウの中央に開くので、AutoCAD に前の位置を保存させない（既定ではプロファイルの Dialogs に書き、次に開くときに使う）
        Application.ShowModalWindow(Application.MainWindow.Handle, dialog, false);
#else
        var dialog = new CommentDialog(form, dark: false);
        dialog.ShowDialogOwnedBy(System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle);
#endif
        dialog.ThrowIfFailed();
        return dialog.DialogResult == true;
    }

#if CADPETA_ACAD
    // AutoCAD の配色。COLORTHEME は 0 = 暗い、1 = 明るい（値を読むだけで、変えない）
    static bool IsDarkTheme() =>
        Convert.ToInt16(Application.GetSystemVariable("COLORTHEME"), CultureInfo.InvariantCulture) == 0;
#endif
}