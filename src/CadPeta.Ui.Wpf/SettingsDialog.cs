using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CadPeta.UseCases;
using static CadPeta.Ui.Wpf.DialogStyle;

namespace CadPeta.Ui.Wpf;

/// <summary>
/// PETAOPT の設定のダイアログ。プロツールとして、項目名を左・入力欄を右に置いた 3 行のフォームにする
/// （形式と PNG の解像度、出力先、ファイル名）。説明はツールチップに置き、画面の文は最小限にする。
/// 入力の検証と設定への変換は <see cref="SettingsForm"/> が行う。ファイル名の下の 1 行に、ふだんはファイル名のプレビューを、
/// 使えない値があればその理由を出す（OK は押せない。コメントのダイアログと同じ形で、メッセージだけの行は持たない）。
/// OK で <c>save</c> を呼び、保存できなければ理由を同じ行に出して開いたままにする。
/// 開いた側は、閉じた後に <see cref="ThrowIfFailed"/> を呼ぶ（イベントの処理の想定外の例外を投げ直す）。
/// </summary>
public sealed class SettingsDialog : Window
{
    // 部品の大きさ（px）。項目名の列、PNG の解像度の欄の幅、パスの欄の幅（ほかは DialogStyle）
    const double LabelWidth = 72;
    const double DpiBoxWidth = 48;
    const double PathBoxWidth = 330;

    readonly SettingsForm _form;
    readonly Func<CadPetaSettings, string?> _save;
    readonly DialogGuard _guard;
    readonly DialogStyle _style;
    readonly Palette _palette;
    readonly string _drawing;
    readonly string _tab;
    readonly DateTime _now;
    readonly TextBlock _status;
    readonly TextBox _dpi;
    readonly TextBox _name;
    readonly Button _ok;

    /// <param name="settingsPath">設定のファイルの場所（表示するだけ）。</param>
    /// <param name="drawing">ファイル名のプレビューに使う図面の名前。</param>
    /// <param name="tab">ファイル名のプレビューに使うタブの名前（モデルなら Model）。</param>
    /// <param name="dark">CAD の配色が暗いか。</param>
    /// <param name="save">設定を保存する。保存できなければ理由を返す（保存できれば null）。</param>
    public SettingsDialog(CadPetaSettings settings, string settingsPath, string drawing, string tab, bool dark,
                          Func<CadPetaSettings, string?> save)
    {
        _form = new SettingsForm(settings);
        _save = save;
        _guard = new DialogGuard(this);
        _style = new DialogStyle(dark);
        _palette = _style.Palette;
        _drawing = drawing;
        _tab = tab;
        _now = DateTime.Now;

        Title = T("dialog.title");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _style.Apply(this);

        var form = new Grid { Margin = new Thickness(12, 12, 12, 0) };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 形式と、PNG の解像度（PNG のチェックボックスの隣。PNG がオフなら入力できない）
        string formatsTip = T("dialog.formats_tip");
        var formats = new StackPanel { Orientation = Orientation.Horizontal, ToolTip = formatsTip };
        foreach (var kind in ArtifactKinds.All)
        {
            var box = new CheckBox
            {
                Content = ArtifactKinds.Label(kind), IsChecked = _form.IsOn(kind), Foreground = _palette.Text,
                VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0),
            };
            AutomationProperties.SetAutomationId(box, AutomationIds.Format(kind));
            box.Checked += _guard.Routed(() => { _form.SetOn(kind, true); Refresh(); });
            box.Unchecked += _guard.Routed(() => { _form.SetOn(kind, false); Refresh(); });
            formats.Children.Add(box);
        }
        _dpi = _style.Input(_form.PngDpi, AutomationIds.PngDpi, T("dialog.dpi_tip", RasterLimitsText()));
        _dpi.Width = DpiBoxWidth;
        _dpi.TextAlignment = TextAlignment.Right;
        _dpi.TextChanged += (sender, args) => _guard.Run(() => { _form.PngDpi = _dpi.Text; Refresh(); });
        formats.Children.Add(_dpi);
        formats.Children.Add(new TextBlock { Text = "DPI", Margin = new Thickness(Gap, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        AddRow(form, Label("dialog.formats", formatsTip), formats);

        // 出力先と、フォルダを選ぶボタン
        var folder = _style.Input(_form.OutputFolder, AutomationIds.Folder, T("dialog.folder_tip"));
        folder.Width = PathBoxWidth;
        folder.TextChanged += (sender, args) => _guard.Run(() => { _form.OutputFolder = folder.Text; Refresh(); });
        var browse = _style.Button("…", T("dialog.browse"));
        browse.Width = FieldHeight + Gap;
        browse.Padding = new Thickness(0); // 1 文字のボタンは余白を取ると文字が切れる
        browse.Click += _guard.Routed(() => Browse(folder));
        AddRow(form, Label("dialog.folder", T("dialog.folder_tip")), Row(folder, browse));

        // ファイル名と、差し込みのメニュー（選んだ差し込みを入力の位置に入れる）
        _name = _style.Input(_form.FileName, AutomationIds.FileName, NameTip());
        _name.Width = PathBoxWidth - ButtonMinWidth + FieldHeight;
        _name.TextChanged += (sender, args) => _guard.Run(() => { _form.FileName = _name.Text; Refresh(); });
        var insert = _style.Button(T("dialog.insert"), T("dialog.insert_tip"));
        insert.Width = ButtonMinWidth - Gap;
        insert.ContextMenu = TokenMenu();
        insert.Click += _guard.Routed(() => { insert.ContextMenu.PlacementTarget = insert; insert.ContextMenu.IsOpen = true; });
        AddRow(form, Label("dialog.name", NameTip()), Row(_name, insert));

        // ファイル名の下の 1 行: 使える値ならファイル名のプレビュー、使えない値か保存の失敗があればその理由（コメントのダイアログと同じ）。
        // 1 行の高さを常に確保し、内容が変わってもほかの部品を動かさない
        _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = PathBoxWidth + FieldHeight };
        _status.Height = Math.Ceiling(_status.FontSize * _status.FontFamily.LineSpacing);
        AutomationProperties.SetAutomationId(_status, AutomationIds.Message);
        AddRow(form, new TextBlock(), _status);

        // 一番下の行: 設定のファイルの場所（全体はツールチップ）、OK、キャンセル
        var file = new TextBlock
        {
            Text = settingsPath, ToolTip = settingsPath, Foreground = _palette.Muted, FontSize = DialogStyle.FontSize - 1,
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = LabelWidth + PathBoxWidth - 2 * ButtonMinWidth,
        };
        AutomationProperties.SetAutomationId(file, AutomationIds.SettingsFile);
        _ok = _style.Button(T("dialog.ok"), null, primary: true);
        _ok.IsDefault = true;
        AutomationProperties.SetAutomationId(_ok, AutomationIds.Ok);
        _ok.Click += _guard.Routed(Save);
        var cancel = _style.Button(T("dialog.cancel"), null);
        cancel.IsCancel = true;
        AutomationProperties.SetAutomationId(cancel, AutomationIds.Cancel);
        // 上の行（ファイル名の下の行）が下に 8 の間を取るので、上の余白は取らない
        var bottom = new DockPanel { Margin = new Thickness(12, 0, 12, 12), LastChildFill = true };
        var buttons = Row(_ok, cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bottom.Children.Add(buttons);
        bottom.Children.Add(file);

        var root = new StackPanel();
        root.Children.Add(form);
        root.Children.Add(bottom);
        Content = root;
        Refresh();
    }

    /// <summary>OK で保存できたか。</summary>
    public bool Saved { get; private set; }

    /// <summary>イベントの処理で想定外の例外が起きていたら投げ直す。ダイアログを閉じた直後に呼ぶ。</summary>
    public void ThrowIfFailed() => _guard.ThrowIfFailed();

    void Refresh()
    {
        _dpi.IsEnabled = _form.IsOn(ArtifactKind.Png);
        var errors = new[] { _form.OutputFolderError, _form.FileNameError, _form.PngDpiError }.OfType<string>().ToList();
        _ok.IsEnabled = errors.Count == 0 && _form.ToSettings() != null;
        if (errors.Count > 0)
        {
            ShowError(errors.Count == 1 ? errors[0] : T("dialog.errors", errors[0], errors.Count - 1), string.Join(Environment.NewLine, errors));
            return;
        }
        string preview = _form.Example(_drawing, _tab, _now);
        _status.Text = preview.Length == 0 ? "" : T("dialog.preview", preview);
        _status.ToolTip = _status.Text.Length == 0 ? null : _status.Text;
        _status.Foreground = _palette.Muted;
    }

    // ファイル名の下の行に理由を出す（全部の理由はツールチップ）
    void ShowError(string text, string tip)
    {
        _status.Text = text;
        _status.ToolTip = tip;
        _status.Foreground = _palette.Error;
    }

    void Save()
    {
        var settings = _form.ToSettings();
        if (settings == null) return;
        string? error = _save(settings);
        if (error != null)
        {
            ShowError(error, error);
            return;
        }
        Saved = true;
        DialogResult = true;
    }

    void Browse(TextBox folder)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = T("dialog.folder") };
        if (_form.FullOutputFolder is { } start && System.IO.Directory.Exists(start))
            dialog.SelectedPath = start;
        if (dialog.ShowDialog(FormsOwner(this)) == System.Windows.Forms.DialogResult.OK)
            folder.Text = dialog.SelectedPath;
    }

    // 差し込みのメニュー。項目は差し込みとその意味で、選ぶと入力の位置に差し込みを入れる
    ContextMenu TokenMenu()
    {
        var menu = new ContextMenu();
        foreach (string token in FileNameTemplate.AllTokens)
        {
            var item = new MenuItem { Header = token + "  " + T(TokenKey(token)) };
            item.Click += _guard.Routed(() =>
            {
                int caret = _name.CaretIndex;
                _name.Text = _name.Text.Insert(caret, token);
                _name.CaretIndex = caret + token.Length;
                _name.Focus();
            });
            menu.Items.Add(item);
        }
        return menu;
    }

    // ファイル名のツールチップ: 差し込みの意味の一覧と、同じ名前があれば (1) を付けること
    static string NameTip() =>
        T("dialog.name_tip") + Environment.NewLine +
        string.Join(Environment.NewLine, FileNameTemplate.AllTokens.Select(token => token + "  " + T(TokenKey(token))));

    static string TokenKey(string token) => "dialog.token." + token.Trim('{', '}');

    static string RasterLimitsText() =>
        T("dialog.dpi_range", CadPeta.Geometry.RasterLimits.MinDpi, CadPetaSettings.MaxPngDpi);

    static void AddRow(Grid grid, UIElement label, UIElement content)
    {
        int row = grid.RowDefinitions.Count;
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(label, row);
        Grid.SetRow(content, row);
        Grid.SetColumn(content, 1);
        if (content is FrameworkElement element) element.Margin = new Thickness(0, 0, 0, 8);
        grid.Children.Add(label);
        grid.Children.Add(content);
    }

    static TextBlock Label(string key, string tip) =>
        new() { Text = T(key), ToolTip = tip, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 0, 0) };

    /// <summary>UI オートメーションで部品を探す Id（画面の確認の試験が使う。言語で変えない）。</summary>
    public static class AutomationIds
    {
        public const string Folder = "CadPeta.Settings.Folder";
        public const string FileName = "CadPeta.Settings.FileName";
        public const string PngDpi = "CadPeta.Settings.PngDpi";
        public const string Message = "CadPeta.Settings.Message";
        public const string Ok = "CadPeta.Settings.Ok";
        public const string Cancel = "CadPeta.Settings.Cancel";
        public const string SettingsFile = "CadPeta.Settings.File";

        /// <summary>形式のチェックボックス（CadPeta.Settings.Format.Pdf など）。</summary>
        public static string Format(ArtifactKind kind) => "CadPeta.Settings.Format." + kind;
    }
}
