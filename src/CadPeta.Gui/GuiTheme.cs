using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using CadPeta.Ui.Wpf;
using Microsoft.Win32;

namespace CadPeta.Gui;

/// <summary>
/// メイン画面の配色と部品。配色と入力欄・ボタンは PETAOPT のダイアログ（<see cref="DialogStyle"/>）と同じにし、
/// 画面の切り替えのボタン（色の 3 つの選択肢、タブ）と色見本だけをここで作る。
/// </summary>
internal sealed class GuiTheme
{
    public GuiTheme(bool dark) { Dark = dark; Style = new DialogStyle(dark); }

    public bool Dark { get; }
    public DialogStyle Style { get; }
    public Palette Palette => Style.Palette;

    /// <summary>Windows のアプリの配色が暗いか（設定の値を読むだけで、変えない。読めなければ明るい配色）。</summary>
    public static bool WindowsUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
    }

    /// <summary>ウィンドウのタイトル バーを暗い配色にする（Windows 10 1809 より前は何もしない）。</summary>
    public void ApplyTitleBar(Window window)
    {
        if (!Dark) return;
        const int DwmwaUseImmersiveDarkMode = 20;
        int on = 1;
        // 失敗しても配色が明るいままになるだけなので、戻り値は使わない
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>白い文字の主のボタン（クリップボードにコピー）。</summary>
    public Button PrimaryButton(string text, string tip)
    {
        var button = Style.Button(text, tip, primary: true);
        button.Background = Palette.Primary;
        button.BorderBrush = Palette.Primary;
        button.FontWeight = FontWeights.SemiBold;
        return button;
    }

    /// <summary>項目のまとまりの見出し。</summary>
    public TextBlock Header(string text) => new()
    {
        Text = text, Foreground = Palette.Muted, FontSize = DialogStyle.FontSize - 1, FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, DialogStyle.Gap),
    };

    public TextBlock Muted(string text) => new() { Text = text, Foreground = Palette.Muted, VerticalAlignment = VerticalAlignment.Center };

    public CheckBox Check(string text, string? tip = null) => new()
    {
        Content = text, Foreground = Palette.Text, ToolTip = tip, VerticalContentAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, DialogStyle.Gap),
    };

    public Border Rule() => new() { Height = 1, Background = Palette.Border, Margin = new Thickness(0, 6, 0, 8) };

    /// <summary>つないだボタンの 1 つ（選ぶと主の青で塗る）。色の 3 つの選択肢に使う。</summary>
    public RadioButton Segment(string text, string group) =>
        Toggle(new RadioButton { Content = text, GroupName = group }, selectedFill: true);

    /// <summary>タブの見出し（選ぶと下に青の線を引く）。</summary>
    public RadioButton Tab(string text, string group) =>
        Toggle(new RadioButton { Content = text, GroupName = group }, selectedFill: false);

    RadioButton Toggle(RadioButton button, bool selectedFill)
    {
        button.Foreground = selectedFill ? Palette.Text : Palette.Muted;
        button.Background = selectedFill ? Palette.Button : Brushes.Transparent;
        button.BorderBrush = selectedFill ? Palette.Border : Brushes.Transparent;
        button.Padding = selectedFill ? new Thickness(10, 0, 10, 0) : new Thickness(14, 0, 14, 0);
        button.Height = selectedFill ? DialogStyle.FieldHeight : DialogStyle.FieldHeight + 6;
        var template = Style.FramedTemplate(typeof(RadioButton), selectedFill ? new Thickness(1) : new Thickness(0, 0, 0, 2));
        // 部品に直接設定した色は、テンプレートのトリガーより優先されるので、選んだときの色はテンプレートの枠に設定する
        var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
        if (selectedFill)
        {
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, Palette.Primary, "border"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Primary, "border"));
            checkedTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, Brushes.White, "border"));
        }
        else
        {
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "border"));
            checkedTrigger.Setters.Add(new Setter(TextElement.ForegroundProperty, Palette.Text, "border"));
        }
        template.Triggers.Add(checkedTrigger);
        button.Template = template;
        return button;
    }

    /// <summary>色見本のボタン（押すと色を選ぶ）。</summary>
    public Button Swatch(string name, double width = 16)
    {
        var button = new Button { Width = width, Height = 16, BorderBrush = Palette.Border, BorderThickness = new Thickness(1), Padding = new Thickness(0) };
        AutomationProperties.SetName(button, name);
        button.Template = Style.FramedTemplate(typeof(Button), new Thickness(1));
        return button;
    }

    public static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
