using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CadPeta.UseCases;

namespace CadPeta.Ui.Wpf;

/// <summary>
/// ダイアログ（設定とコメント）の共通の見た目: ウィンドウの書式、配色、入力欄とボタン。
/// 配色はルートと部品に必ず指定する（WPF は指定しないとシステムの既定の色になる）。CAD の COLORTHEME に合わせて選ぶ。
/// </summary>
public sealed class DialogStyle
{
    // 部品の大きさ（px）。入力欄の高さ、ボタンの最小の幅、部品の間、文字の大きさ
    public const double FieldHeight = 22;
    public const double ButtonMinWidth = 76;
    public const double Gap = 4;
    public const double FontSize = 12;

    /// <summary>画面の文（今の言語の文言に引数を差し込んだもの）。</summary>
    public static string T(string key, params object[] args) => MessageCatalogs.Current.Format(key, args);

    public DialogStyle(bool dark) => Palette = dark ? Palette.Dark : Palette.Light;

    public Palette Palette { get; }

    /// <summary>ウィンドウの書式（内容に合わせた大きさ、大きさを変えない、タスク バーに出さない、文字と配色）。</summary>
    public void Apply(Window window)
    {
        window.SizeToContent = SizeToContent.WidthAndHeight;
        window.ResizeMode = ResizeMode.NoResize;
        window.ShowInTaskbar = false;
        window.UseLayoutRounding = true;
        window.FontFamily = new FontFamily("Yu Gothic UI, Segoe UI, Meiryo UI");
        window.FontSize = FontSize;
        window.Background = Palette.Window;
        window.Foreground = Palette.Text;
    }

    public TextBox Input(string text, string automationId, string? tip)
    {
        var box = new TextBox
        {
            Text = text, Height = FieldHeight, Padding = new Thickness(2, 0, 2, 0), VerticalContentAlignment = VerticalAlignment.Center,
            Background = Palette.Field, Foreground = Palette.Text, BorderBrush = Palette.Border, CaretBrush = Palette.Text, ToolTip = tip,
        };
        AutomationProperties.SetAutomationId(box, automationId);
        return box;
    }

    // ボタン。配色を持つので、既定のテンプレート（明るい色に変わる）を使わず、枠と文字だけのテンプレートを持つ
    public Button Button(string text, string? tip, bool primary = false) =>
        new()
        {
            Content = text, ToolTip = tip, Height = FieldHeight + 2, MinWidth = text.Length > 1 ? ButtonMinWidth : 0,
            Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(Gap, 0, 0, 0),
            Background = primary ? Palette.Accent : Palette.Button, Foreground = primary ? Brushes.White : Palette.Text,
            BorderBrush = primary ? Palette.Accent : Palette.Border, Template = ButtonTemplate(),
        };

    ControlTemplate ButtonTemplate() => FramedTemplate(typeof(Button), new Thickness(1));

    /// <summary>
    /// 枠と中身だけのテンプレート（部品の背景・枠の色・余白を使い、中身を中央に置く。マウスが乗ると枠を青にし、使えないときは薄くする）。
    /// 配色を持つ部品は、既定のテンプレート（明るい色に変わる）の代わりにこれを使う。枠の要素の名前は "border"（トリガーの対象に使う）。
    /// </summary>
    public ControlTemplate FramedTemplate(Type controlType, Thickness borderThickness)
    {
        var border = new FrameworkElementFactory(typeof(Border), "border");
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Control.BackgroundProperty));
        border.SetValue(Border.BorderBrushProperty, new TemplateBindingExtension(Control.BorderBrushProperty));
        border.SetValue(Border.BorderThicknessProperty, borderThickness);
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(controlType) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BorderBrushProperty, Palette.Accent, "border"));
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.5));
        template.Triggers.Add(hover);
        template.Triggers.Add(disabled);
        return template;
    }

    /// <summary>部品を横に並べる。</summary>
    public static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var item in items) row.Children.Add(item);
        return row;
    }

    /// <summary>Windows フォームの画面（フォルダや色の選択）の所有者にする、WPF のウィンドウのハンドル。</summary>
    public static System.Windows.Forms.IWin32Window FormsOwner(Window window) => new OwnerHandle(new WindowInteropHelper(window).Handle);

    sealed class OwnerHandle : System.Windows.Forms.IWin32Window
    {
        public OwnerHandle(IntPtr handle) => Handle = handle;
        public IntPtr Handle { get; }
    }
}
/// <summary>ダイアログの配色（暗い配色と明るい配色）。</summary>
public sealed class Palette
{
    public static readonly Palette Dark = new(0x2B2F36, 0x1F2328, 0x555D6B, 0x363C46, 0xE8EAED, 0xA9B0BB, 0xFF8A7A, 0x1F2328);
    public static readonly Palette Light = new(0xF7F8FA, 0xFFFFFF, 0xA9B1BD, 0xE9ECF0, 0x1E2228, 0x5A6270, 0xC23A2B, 0xDDE1E6);

    // シリーズのアイコンと同じ青
    public Brush Accent { get; } = Frozen(0x2E7FD9);
    // 白い文字を載せる塗りの青（シリーズの青より濃くし、白い文字とのコントラストを 4.5:1 以上にする）
    public Brush Primary { get; } = Frozen(0x1F6FC5);
    public Brush Window { get; }
    public Brush Field { get; }
    public Brush Border { get; }
    public Brush Button { get; }
    public Brush Text { get; }
    public Brush Muted { get; }
    public Brush Error { get; }
    // 白い用紙（図のプレビュー）の周りの色。明るい配色でも用紙の境目が見えるように、入力欄の白と分ける
    public Brush Canvas { get; }

    Palette(int window, int field, int border, int button, int text, int muted, int error, int canvas)
    {
        Window = Frozen(window); Field = Frozen(field); Border = Frozen(border); Button = Frozen(button);
        Text = Frozen(text); Muted = Frozen(muted); Error = Frozen(error); Canvas = Frozen(canvas);
    }

    static Brush Frozen(int rgb)
    {
        var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        brush.Freeze();
        return brush;
    }
}
