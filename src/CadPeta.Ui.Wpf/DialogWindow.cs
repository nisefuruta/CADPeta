using System;
using System.Windows;
using System.Windows.Interop;

namespace CadPeta.Ui.Wpf;

/// <summary>ダイアログ（設定とコメント）を開く処理。</summary>
public static class DialogWindow
{
    /// <summary>所有者のウィンドウを指定してモーダルで開く（ShowModalWindow の無い CAD 用）。所有者は表示の前に指定する。</summary>
    public static void ShowDialogOwnedBy(this Window dialog, IntPtr owner)
    {
        new WindowInteropHelper(dialog) { Owner = owner };
        dialog.ShowDialog();
    }
}
