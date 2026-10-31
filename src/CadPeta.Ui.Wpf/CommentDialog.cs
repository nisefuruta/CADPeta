using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CadPeta.UseCases;
using static CadPeta.Ui.Wpf.DialogStyle;

namespace CadPeta.Ui.Wpf;

/// <summary>
/// PETA のファイル名のコメントのダイアログ。範囲を指定した直後に目に入るよう、CAD のウィンドウ（所有者）の中央に開き、入力欄にフォーカスを置く。
/// カーソルの位置には開かない（画面の端ではボタンがカーソルの下に来て続けたクリックで押される、CAD と別のモニターに出る）。
/// Enter で OK、Esc でキャンセル。
/// 入力欄の下の 1 行に、入力したコメントを差し込んだファイル名を表示し、使えないコメントでは同じ行に理由を出して OK を押せなくする。
/// 検証とファイル名は <see cref="CommentForm"/> が作り、ここは表示だけを持つ。
/// 開いた側は、閉じた後に <see cref="ThrowIfFailed"/> を呼ぶ（イベントの処理の想定外の例外を投げ直す）。
/// </summary>
public sealed class CommentDialog : Window
{
    // 部品の大きさ（px）。項目名の列、入力欄の幅、ダイアログの内側の余白
    const double LabelWidth = 56;
    const double InputWidth = 230;
    const double Pad = 8;

    readonly CommentForm _form;
    readonly DialogGuard _guard;
    readonly Palette _palette;
    readonly TextBox _input;
    readonly TextBlock _status;
    readonly Button _ok;

    /// <param name="dark">CAD の配色が暗いか。</param>
    public CommentDialog(CommentForm form, bool dark)
    {
        _form = form;
        _guard = new DialogGuard(this);
        var style = new DialogStyle(dark);
        _palette = style.Palette;

        Title = T("comment.dialog_title");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        style.Apply(this);

        _input = style.Input(form.Comment, AutomationIds.Input, null);
        _input.Width = InputWidth;
        _input.Margin = new Thickness(Gap, 0, 0, 0);
        _input.TextChanged += (sender, args) => _guard.Run(() => { _form.Comment = _input.Text; Refresh(); });
        _ok = style.Button(T("dialog.ok"), null, primary: true);
        _ok.IsDefault = true;
        AutomationProperties.SetAutomationId(_ok, AutomationIds.Ok);
        _ok.Click += _guard.Routed(Accept);
        var cancel = style.Button(T("dialog.cancel"), null);
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(cancel, AutomationIds.Cancel);
        var label = new TextBlock { Text = T("comment.dialog_label"), Width = LabelWidth, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in new UIElement[] { label, _input, _ok, cancel }) row.Children.Add(item);

        // ファイル名か理由の 1 行。入力欄の左端にそろえ、長ければ末尾を省略する（全体はツールチップ）
        _status = new TextBlock
        {
            TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(LabelWidth + Gap, Gap, 0, 0),
            MaxWidth = InputWidth + 2 * (ButtonMinWidth + Gap),
        };
        _status.Height = Math.Ceiling(_status.FontSize * _status.FontFamily.LineSpacing);
        AutomationProperties.SetAutomationId(_status, AutomationIds.Status);

        var root = new StackPanel { Margin = new Thickness(Pad) };
        root.Children.Add(row);
        root.Children.Add(_status);
        Content = root;
        Loaded += _guard.Routed(() => _input.Focus());
        Refresh();
    }

    /// <summary>イベントの処理で想定外の例外が起きていたら投げ直す。ダイアログを閉じた直後に呼ぶ。</summary>
    public void ThrowIfFailed() => _guard.ThrowIfFailed();

    void Refresh()
    {
        string? error = _form.Error;
        _ok.IsEnabled = error == null;
        _status.Text = error ?? T("dialog.preview", _form.Preview);
        _status.Foreground = error == null ? _palette.Muted : _palette.Error;
        _status.ToolTip = _status.Text;
    }

    void Accept()
    {
        if (_form.Error != null) return;
        DialogResult = true;
    }

    /// <summary>UI オートメーションで部品を探す Id（画面の確認の試験が使う。言語で変えない）。</summary>
    public static class AutomationIds
    {
        public const string Input = "CadPeta.Comment.Input";
        public const string Status = "CadPeta.Comment.Status";
        public const string Ok = "CadPeta.Comment.Ok";
        public const string Cancel = "CadPeta.Comment.Cancel";
    }
}
