using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CadPeta.Acquire.Pdf;
using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;
using CadPeta.Ui.Wpf;
using CadPeta.UseCases;
using DrawingPoint = System.Drawing.PointF;
using Rectangle = System.Windows.Shapes.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace CadPeta.Gui;

/// <summary>
/// cadpeta-gui.exe のメイン画面。上のツールバーに入力の PDF・ページ・保存・コピーを置き、左に変換の設定、右にプレビュー、
/// 下にステータス バーを置く。設定を変えると、少し待ってからプレビューを作り直す。
/// 判定（保存できるか、プレビューを反映してよいか）は <see cref="PreviewPolicy"/>、入力欄の解釈は <see cref="GuiInputs"/> と
/// <see cref="ColorReplacementRows"/> が行い、ここは表示と操作の受け付けだけを持つ。
/// </summary>
public sealed class MainWindow : Window
{
    // 部品の大きさ（px）。左の設定の欄の幅、パスの欄の幅
    const double SettingsWidth = 300;
    const double PathWidth = 330;
    // プレビューの用紙の周りの余白（px）
    const double PreviewPadding = 12;
    // 設定を変えてからプレビューを作り直すまでの待ち（入力の途中で何度も作り直さない）
    static readonly TimeSpan PreviewDelay = TimeSpan.FromMilliseconds(300);
    // スポイトが、クリックした位置に図形が無いときに近くを探す範囲（画面の px）
    const double PickRadiusLogicalPx = 4;
    const double MinCropDragLogicalPx = 4;
    // 保存する形式の表示の順（画面の一覧とファイルの種類の一覧で共通）
    static readonly ArtifactKind[] SaveFormats = { ArtifactKind.Emf, ArtifactKind.Svg, ArtifactKind.Pdf, ArtifactKind.Png };

    readonly GuiTheme _theme;
    readonly Palette _palette;
    readonly TextBox _input;
    readonly TextBox _page;
    readonly TextBlock _pageCountText;
    readonly Button _prevPage, _nextPage, _save, _copy, _selectCrop, _pickColor;
    readonly RadioButton _none, _mono, _gray;
    readonly CheckBox _keepBg, _outline, _layers, _crop;
    readonly TextBox[] _cropValues;
    readonly TextBox _margin, _dpi;
    readonly Dictionary<ArtifactKind, CheckBox> _formats = new();
    readonly Button _emfBackgroundSwatch;
    readonly StackPanel _rowsPanel = new();
    readonly List<ReplacementRow> _rows = new();
    readonly TextBlock _matchSummary, _paletteSummary;
    readonly PaletteView _paletteView;
    readonly Grid _previewHost = new() { ClipToBounds = true };
    readonly Image _preview = new() { Stretch = Stretch.Uniform };
    readonly Canvas _overlay = new() { IsHitTestVisible = false };
    readonly Rectangle _selection;
    readonly StackPanel _diagnostics = new();
    readonly TextBox _log;
    readonly RadioButton _diagnosticsTab;
    readonly TextBlock _status, _pageInfo, _sizeInfo, _backgroundInfo;
    readonly DispatcherTimer _debounce = new() { Interval = PreviewDelay };
    readonly SemaphoreSlim _runGate = new(1, 1);
    readonly GuiSession _session = new();
    readonly CancellationTokenSource _shutdownCts = new();
    CancellationTokenSource? _previewCts;
    IrRgb _emfBackground = new(1, 1, 1);
    ReplacementRow? _currentRow;
    // 開いた PDF（入力の欄は入力の途中の文字を持つので、変換にはこれを使う）
    string _pdf = "";
    // PDF を選んだ回数（ページ数を読む間に次の PDF が選ばれたかを見分ける）
    int _pdfSelection;
    int _pageCount;
    int _requestedPage = 1;
    int _generation;
    bool _closing, _shutdownReady, _busy;
    PreviewMode _previewMode;
    DrawingPoint? _dragStartImage, _dragCurrentImage;
    PreviewSnapshot? _snapshot;
    PdfPetaPickMap? _pickMap;
    IReadOnlyList<ColorUse> _paletteShown = Array.Empty<ColorUse>();

    public MainWindow()
    {
        _theme = new GuiTheme(GuiTheme.WindowsUsesDarkTheme());
        _palette = _theme.Palette;
        var style = _theme.Style;
        Title = "CADPeta PDF"; Width = 1180; Height = 760; MinWidth = 900; MinHeight = 600;
        AllowDrop = true; UseLayoutRounding = true;
        FontFamily = new FontFamily("Yu Gothic UI, Segoe UI, Meiryo UI"); FontSize = DialogStyle.FontSize;
        Background = _palette.Window; Foreground = _palette.Text;
        LoadIcon();

        _input = style.Input("", "Input", "PDF のファイル。ウィンドウへドロップしても開けます。");
        _input.Width = PathWidth;
        _page = style.Input("1", "Page", "変換するページ。PageUp・PageDown でも切り替えられます。");
        _page.Width = 40; _page.TextAlignment = TextAlignment.Center;
        _pageCountText = _theme.Muted("/ -");
        _prevPage = style.Button("‹", "前のページ"); _nextPage = style.Button("›", "次のページ");
        foreach (var b in new[] { _prevPage, _nextPage }) { b.Width = DialogStyle.FieldHeight + 2; b.Padding = new Thickness(0); }
        AutomationProperties.SetName(_prevPage, "前のページ"); AutomationProperties.SetName(_nextPage, "次のページ");
        _save = style.Button("保存…", "チェックした形式で保存します。");
        _copy = _theme.PrimaryButton("クリップボードにコピー", "EMF をクリップボードにコピーします。Word・Excel などに貼り付けられます。");

        _none = _theme.Segment("そのまま", "color"); _mono = _theme.Segment("モノクロ", "color"); _gray = _theme.Segment("グレー", "color");
        _none.IsChecked = true;
        _mono.ToolTip = "紙面の白はそのまま、ほかの色を黒にします。"; _gray.ToolTip = "明るさを保った灰色にします。";
        _keepBg = _theme.Check("背景を残す", "用紙の全面を覆う背景の塗りを残します（既定は除去）。");
        _outline = _theme.Check("文字をアウトライン化", "文字を輪郭の図形にします。フォントが無い PC でも見た目が変わりませんが、文字は編集できなくなります。");
        _layers = _theme.Check("レイヤ別にグループ化", "図形をレイヤごとにまとめます。描く順番が変わり、重なりの見た目が変わることがあります。");
        _crop = _theme.Check("範囲を切り出す"); _crop.FontWeight = FontWeights.SemiBold;
        _cropValues = new[] { "0", "0", "100", "100" }.Select((v, i) => NumberBox(v, $"Crop{i}")).ToArray();
        _margin = NumberBox("0", "Margin");
        _selectCrop = style.Button("ドラッグで指定", "プレビューの上をドラッグして範囲を指定します（Esc で解除）。");
        _selectCrop.Margin = new Thickness(0);
        _pickColor = style.Button("スポイト", "プレビューの線や塗りをクリックして、置換元の色にします（Esc で解除）。");
        _matchSummary = _theme.Muted(""); _matchSummary.TextWrapping = TextWrapping.Wrap;
        _paletteSummary = _theme.Muted("使用色: プレビューの作成後に表示");
        _paletteSummary.ToolTip = "色見本をクリックすると、置換元の色にします。";
        _paletteView = new PaletteView(_palette.Border);
        _dpi = NumberBox(CadPetaSettings.DefaultPngDpi.ToString(System.Globalization.CultureInfo.InvariantCulture), "PngDpi");
        _dpi.Width = 48; _dpi.ToolTip = $"PNG の解像度（{RasterLimits.MinDpi}～{CadPetaSettings.MaxPngDpi}）";
        _emfBackgroundSwatch = _theme.Swatch("EMF の背景色", 40);
        _emfBackgroundSwatch.Background = Brushes.White; _emfBackgroundSwatch.ToolTip = "EMF の背景色（クリックで選ぶ）";

        _selection = new Rectangle { Stroke = _palette.Accent, StrokeThickness = 2, StrokeDashArray = { 4, 2 },
            Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0x2E, 0x7F, 0xD9)), Visibility = Visibility.Collapsed };
        _overlay.Children.Add(_selection);
        AutomationProperties.SetAutomationId(_preview, "Preview");
        _log = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, AcceptsReturn = true, BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = _palette.Field, Foreground = _palette.Text, FontFamily = new FontFamily("Consolas, Yu Gothic UI") };
        _diagnosticsTab = _theme.Tab("診断", "tabs");
        _status = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAutomationId(_status, "Status");
        _pageInfo = _theme.Muted(""); _sizeInfo = _theme.Muted(""); _backgroundInfo = _theme.Muted("");

        Content = BuildLayout();
        WireEvents();
        UpdateUiState();
    }

    void LoadIcon()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("CadPeta.Gui.app.ico");
        // アイコンが無ければ Windows の既定のアイコンになるだけなので、読めなくても続ける
        if (stream is null) return;
        try { Icon = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        { System.Diagnostics.Debug.WriteLine($"アプリのアイコンを読めないため既定のアイコンを使う: {ex.Message}"); }
    }

    TextBox NumberBox(string text, string automationId)
    {
        var box = _theme.Style.Input(text, automationId, null);
        box.TextAlignment = TextAlignment.Right;
        return box;
    }

    UIElement BuildLayout()
    {
        var root = new DockPanel();
        var inputs = CenteredRow(new TextBlock { Text = "PDF", Foreground = _palette.Muted, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
            _input, Browse(), Separator(), _theme.Muted("ページ"), Spaced(_prevPage, 6), Spaced(_page, DialogStyle.Gap), _nextPage,
            Spaced(_pageCountText, 6));
        var actions = CenteredRow(_save, _copy);
        var toolbarGrid = new Grid { Height = 40, Margin = new Thickness(10, 0, 10, 0) };
        toolbarGrid.Children.Add(inputs);
        actions.HorizontalAlignment = HorizontalAlignment.Right;
        toolbarGrid.Children.Add(actions);
        var top = new Border { BorderBrush = _palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Child = toolbarGrid };
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var statusBar = new Border { BorderBrush = _palette.Border, BorderThickness = new Thickness(0, 1, 0, 0), Background = _palette.Field, Height = 24 };
        var statusGrid = new Grid { Margin = new Thickness(10, 0, 10, 0) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition());
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var infos = CenteredRow(_pageInfo, Spaced(_sizeInfo, 16), Spaced(_backgroundInfo, 16));
        Grid.SetColumn(infos, 1);
        statusGrid.Children.Add(_status); statusGrid.Children.Add(infos);
        statusBar.Child = statusGrid;
        DockPanel.SetDock(statusBar, Dock.Bottom); root.Children.Add(statusBar);

        var settings = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = SettingsWidth,
            Content = BuildSettings(), BorderBrush = _palette.Border };
        var settingsBorder = new Border { BorderBrush = _palette.Border, BorderThickness = new Thickness(0, 0, 1, 0), Child = settings };
        DockPanel.SetDock(settingsBorder, Dock.Left); root.Children.Add(settingsBorder);
        root.Children.Add(BuildPreviewArea());
        return root;
    }

    Button Browse()
    {
        var browse = _theme.Style.Button("参照…", "PDF のファイルを選びます。");
        browse.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf" };
            if (dialog.ShowDialog(this) == true) SelectInput(dialog.FileName);
        };
        return browse;
    }

    UIElement Separator() => new Border { Width = 1, Height = 22, Background = _palette.Border, Margin = new Thickness(10, 0, 10, 0) };

    // 部品を横に並べ、行の高さの中で上下の中央に置く
    static StackPanel CenteredRow(params UIElement[] children)
    {
        var row = DialogStyle.Row(children);
        row.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    static T Spaced<T>(T element, double left) where T : FrameworkElement { element.Margin = new Thickness(left, 0, 0, 0); return element; }

    UIElement BuildSettings()
    {
        var panel = new StackPanel { Margin = new Thickness(10, 8, 10, 10) };

        panel.Children.Add(_theme.Header("変換"));
        var colorRow = CenteredRow(new TextBlock { Text = "色", Width = 40, VerticalAlignment = VerticalAlignment.Center }, _none, _mono, _gray);
        colorRow.Margin = new Thickness(0, 0, 0, 6);
        panel.Children.Add(colorRow);
        panel.Children.Add(_keepBg); panel.Children.Add(_outline); panel.Children.Add(_layers);
        panel.Children.Add(_theme.Rule());

        var cropHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(_selectCrop, Dock.Right);
        cropHeader.Children.Add(_selectCrop); cropHeader.Children.Add(_crop);
        _crop.Margin = new Thickness(0); _crop.VerticalAlignment = VerticalAlignment.Center;
        panel.Children.Add(cropHeader);
        var cropGrid = new Grid();
        for (int c = 0; c < 4; c++) cropGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? new GridLength(28) : new GridLength(1, GridUnitType.Star) });
        for (int r = 0; r < 3; r++) cropGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DialogStyle.FieldHeight + DialogStyle.Gap) });
        string[] labels = { "x0", "y0", "x1", "y1" };
        for (int i = 0; i < 4; i++) Place(cropGrid, _theme.Muted(labels[i]), i / 2, i % 2 * 2, _cropValues[i], tip: "ページの左上からの位置（mm）");
        Place(cropGrid, _theme.Muted("余白"), 2, 0, _margin, tip: "切り出す範囲の外側に付ける余白（mm）");
        var mm = _theme.Muted("mm"); mm.Margin = new Thickness(8, 0, 0, 0); Grid.SetRow(mm, 2); Grid.SetColumn(mm, 2); cropGrid.Children.Add(mm);
        panel.Children.Add(cropGrid);
        panel.Children.Add(_theme.Rule());

        var replaceHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var add = _theme.Style.Button("追加", "置換の行を追加します。");
        add.Click += (_, _) => { AddRow("#000000", "#000000", "0"); QueuePreview(); };
        var replaceButtons = CenteredRow(_pickColor, add);
        DockPanel.SetDock(replaceButtons, Dock.Right);
        replaceHeader.Children.Add(replaceButtons);
        var replaceTitle = _theme.Header("色置換"); replaceTitle.Margin = new Thickness(0); replaceTitle.VerticalAlignment = VerticalAlignment.Center;
        replaceHeader.Children.Add(replaceTitle);
        panel.Children.Add(replaceHeader);
        panel.Children.Add(_rowsPanel);
        _matchSummary.Margin = new Thickness(0, 2, 0, 6);
        _matchSummary.ToolTip = "件数は、見えている線・塗り・文字の数です（透明なものは数えません）。";
        panel.Children.Add(_matchSummary);
        panel.Children.Add(_paletteSummary);
        panel.Children.Add(new Border
        {
            BorderBrush = _palette.Border, BorderThickness = new Thickness(1), Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(2),
            Child = new ScrollViewer { MaxHeight = 88, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _paletteView },
        });
        panel.Children.Add(_theme.Rule());

        panel.Children.Add(_theme.Header("保存する形式"));
        foreach (var kind in SaveFormats)
        {
            var box = _theme.Check(ArtifactKinds.Label(kind));
            box.Width = 64; box.Margin = new Thickness(0);
            box.IsChecked = kind == ArtifactKind.Emf;
            AutomationProperties.SetAutomationId(box, $"Format{kind}");
            _formats[kind] = box;
            var row = CenteredRow(box);
            row.Margin = new Thickness(0, 0, 0, DialogStyle.Gap);
            if (kind == ArtifactKind.Emf) { row.Children.Add(_theme.Muted("背景色")); row.Children.Add(Spaced(_emfBackgroundSwatch, 6)); }
            if (kind == ArtifactKind.Png) { row.Children.Add(_dpi); row.Children.Add(Spaced(_theme.Muted("dpi"), DialogStyle.Gap)); }
            panel.Children.Add(row);
        }
        return panel;
    }

    static void Place(Grid grid, FrameworkElement label, int row, int column, FrameworkElement field, string tip)
    {
        label.ToolTip = tip; field.ToolTip = tip;
        field.Margin = new Thickness(0, 0, column == 0 ? 10 : 0, DialogStyle.Gap);
        Grid.SetRow(label, row); Grid.SetColumn(label, column);
        Grid.SetRow(field, row); Grid.SetColumn(field, column + 1);
        grid.Children.Add(label); grid.Children.Add(field);
    }

    UIElement BuildPreviewArea()
    {
        var area = new DockPanel();
        var previewTab = _theme.Tab("プレビュー", "tabs"); previewTab.IsChecked = true;
        var logTab = _theme.Tab("ログ", "tabs");
        var tabs = CenteredRow(previewTab, _diagnosticsTab, logTab);
        tabs.VerticalAlignment = VerticalAlignment.Bottom;
        var tabBar = new Border { BorderBrush = _palette.Border, BorderThickness = new Thickness(0, 0, 0, 1), Height = 30, Padding = new Thickness(8, 0, 8, 0), Child = tabs };
        DockPanel.SetDock(tabBar, Dock.Top); area.Children.Add(tabBar);

        _previewHost.Background = _palette.Canvas;
        _previewHost.Children.Add(_preview);
        _previewHost.Children.Add(_overlay);
        var diagnostics = new ScrollViewer { Content = _diagnostics, Padding = new Thickness(10, 6, 10, 6), Background = _palette.Field,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        var pages = new Grid();
        // 用紙の周りに余白を取る。余白はプレビューの部品の外側の枠に付け、部品の中の座標（範囲のドラッグ、スポイト）は変えない
        var previewFrame = new Border { Background = _palette.Canvas, Padding = new Thickness(PreviewPadding), Child = _previewHost };
        pages.Children.Add(previewFrame); pages.Children.Add(diagnostics); pages.Children.Add(_log);
        void Show(UIElement visible) { foreach (UIElement child in pages.Children) child.Visibility = child == visible ? Visibility.Visible : Visibility.Collapsed; }
        previewTab.Checked += (_, _) => Show(previewFrame);
        _diagnosticsTab.Checked += (_, _) => Show(diagnostics);
        logTab.Checked += (_, _) => Show(_log);
        Show(previewFrame);
        area.Children.Add(pages);
        return area;
    }

    void WireEvents()
    {
        DragEnter += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) SelectInput(files[0]); };
        _input.KeyDown += (_, e) => { if (e.Key == Key.Enter) SelectInput(_input.Text); };
        _prevPage.Click += (_, _) => ChangePage(CurrentPage() - 1);
        _nextPage.Click += (_, _) => ChangePage(CurrentPage() + 1);
        _page.KeyDown += (_, e) => { if (e.Key == Key.Enter) ChangePage(_page.Text); };
        _page.LostKeyboardFocus += (_, _) => ChangePage(_page.Text);
        _copy.Click += async (_, _) => await CopyAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _selectCrop.Click += async (_, _) => await ToggleModeAsync(PreviewMode.PreparingCrop);
        _pickColor.Click += async (_, _) => await ToggleModeAsync(PreviewMode.PreparingPickColor);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshPreviewAsync(); };
        foreach (var radio in new[] { _none, _mono, _gray }) radio.Checked += (_, _) => QueuePreview();
        foreach (var check in new[] { _keepBg, _outline, _layers, _crop }) { check.Checked += (_, _) => QueuePreview(); check.Unchecked += (_, _) => QueuePreview(); }
        // 範囲の欄は、切り出しがオンのときだけ入力できる
        foreach (var box in _cropValues.Append(_margin)) box.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = _crop });
        foreach (var box in _cropValues.Append(_margin)) box.TextChanged += (_, _) => { if (_crop.IsChecked == true) QueuePreview(); };
        _emfBackgroundSwatch.Click += (_, _) => PickEmfBackground();
        // PNG の解像度の欄は、PNG を保存するときだけ入力できる（PETAOPT のダイアログと同じ）
        _dpi.SetBinding(IsEnabledProperty, new System.Windows.Data.Binding(nameof(CheckBox.IsChecked)) { Source = _formats[ArtifactKind.Png] });
        _paletteView.Picked += use => CommitPickedColor(use.Color);
        _previewHost.MouseLeftButtonDown += OnPreviewHostMouseDown;
        _previewHost.MouseMove += OnPreviewHostMouseMove;
        _previewHost.MouseLeftButtonUp += OnPreviewHostMouseUp;
        _previewHost.SizeChanged += (_, _) => DrawSelection();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => { if (_previewMode != PreviewMode.Normal) CancelMode(); };
        SourceInitialized += (_, _) => _theme.ApplyTitleBar(this);
        Closing += OnClosing;
        Closed += (_, _) => DisposeResources();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _previewMode != PreviewMode.Normal) { CancelMode(); e.Handled = true; }
        else if (e.Key is Key.PageUp or Key.PageDown && Keyboard.FocusedElement is not TextBox)
        { ChangePage(CurrentPage() + (e.Key == Key.PageDown ? 1 : -1)); e.Handled = true; }
    }

    // ---------------- 入力とページ ----------------

    /// <summary>PDF を開く（起動の引数の PDF）。</summary>
    public void OpenPdf(string path) => SelectInput(path);

    async void SelectInput(string path)
    {
        path = path.Trim().Trim('"');
        if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) { _input.Text = _pdf; ShowStatus("PDF のファイルを指定してください。", error: true); return; }
        // 読めない PDF や、大きな PDF で開くのをやめたときは、前の PDF の状態（ページ、プレビュー）を残し、入力の欄も前の PDF に戻す
        // ページ数を読む間にほかの PDF を選んだら、後で終わった古い読み込みの結果は使わない（最後に選んだ PDF だけを開く）
        int selection = ++_pdfSelection;
        int count;
        try { count = await Task.Run(() => PdfIrAcquirer.CountPages(path)); }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (_closing || selection != _pdfSelection) return;
            _input.Text = _pdf; ShowStatus($"PDF を読めません: {ex.Message}", error: true); return;
        }
        // 読む間に画面を閉じたときは、閉じた画面に確認の画面を出さない
        if (_closing || selection != _pdfSelection) return;
        if (new FileInfo(path).Length > PdfPetaGuiPresenter.LargePdfWarnBytes
            && MessageBox.Show(this, "大きな PDF です。プレビューを作りますか？", "CADPeta", MessageBoxButton.YesNo) != MessageBoxResult.Yes) { _input.Text = _pdf; return; }
        if (_closing || selection != _pdfSelection) return;
        if (_previewMode != PreviewMode.Normal) CancelMode(false);
        _pdf = path;
        _input.Text = path;
        // パスの欄は、ファイル名が見えるように末尾を表示する（文字の配置が決まった後にスクロールする）
        _input.ToolTip = path;
        // スクロールの完了は待たない（表示だけで、ほかの処理はスクロールに依存しない）
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _input.ScrollToHorizontalOffset(double.MaxValue));
        _pageCount = count;
        _requestedPage = 1;
        UpdatePageControls(1);
        QueuePreview();
    }

    // 変換するページ（ページの欄で確定した番号）。プレビュー・保存・コピーはこのページを使う
    int CurrentPage() => _requestedPage;

    void ChangePage(int requested) => ChangePage(requested.ToString(System.Globalization.CultureInfo.InvariantCulture));

    // ページを変えたら、表示中のプレビューのページではなく、最後に選んだページと比べて作り直す
    // （作成中のプレビューのページへ戻したときも、作成中のプレビューを取り消して作り直す）
    void ChangePage(string text)
    {
        var (page, error) = GuiInputs.ParsePage(text, _pageCount);
        if (error != null) { ShowStatus(error, error: true); UpdatePageControls(_requestedPage); return; }
        bool changed = page != _requestedPage;
        _requestedPage = page;
        UpdatePageControls(page);
        if (changed && File.Exists(_pdf)) { if (_previewMode != PreviewMode.Normal) CancelMode(false); QueuePreview(); }
    }
    void UpdatePageControls(int page)
    {
        if (_page.Text != page.ToString()) _page.Text = page.ToString();
        _pageCountText.Text = _pageCount > 0 ? $"/ {_pageCount}" : "/ -";
        _prevPage.IsEnabled = page > 1; _nextPage.IsEnabled = page < _pageCount;
    }

    // ---------------- 色置換の行 ----------------

    sealed class ReplacementRow
    {
        public required StackPanel Element { get; init; }
        public required TextBox From { get; init; }
        public required TextBox To { get; init; }
        public required TextBox Tolerance { get; init; }
    }

    ReplacementRow AddRow(string from, string to, string tolerance)
    {
        var style = _theme.Style;
        TextBox Hex(string text, string name) { var box = style.Input(text, name, null); box.Width = 66; AutomationProperties.SetName(box, name); return box; }
        var fromBox = Hex(from, "変換前"); var toBox = Hex(to, "変換後");
        var toleranceBox = NumberBox(tolerance, "許容差"); toleranceBox.Width = 30;
        toleranceBox.ToolTip = "R・G・B のそれぞれの差がこの値以内の色も置き換えます（0 は同じ色だけ、最大 255）。";
        AutomationProperties.SetName(toleranceBox, "許容差");
        var fromSwatch = _theme.Swatch("変換前の色を選ぶ"); var toSwatch = _theme.Swatch("変換後の色を選ぶ");
        var remove = style.Button("×", "この行を削除します。"); remove.Width = 22; remove.Padding = new Thickness(0); remove.MinWidth = 0;
        AutomationProperties.SetName(remove, "この行を削除");
        var element = CenteredRow(fromSwatch, Spaced(fromBox, 4), Spaced(_theme.Muted("→"), 4), Spaced(toSwatch, 4), Spaced(toBox, 4),
            Spaced(_theme.Muted("±"), 4), Spaced(toleranceBox, 2), remove);
        element.Margin = new Thickness(0, 0, 0, DialogStyle.Gap);
        var row = new ReplacementRow { Element = element, From = fromBox, To = toBox, Tolerance = toleranceBox };
        void Sync(TextBox box, Button swatch)
        {
            swatch.Background = IrRgb.TryParseHex(box.Text.Trim(), out var c) ? ToBrush(c) : Brushes.Transparent;
        }
        fromBox.TextChanged += (_, _) => { Sync(fromBox, fromSwatch); QueuePreview(); };
        toBox.TextChanged += (_, _) => { Sync(toBox, toSwatch); QueuePreview(); };
        toleranceBox.TextChanged += (_, _) => QueuePreview();
        foreach (var box in new[] { fromBox, toBox, toleranceBox }) box.GotKeyboardFocus += (_, _) => _currentRow = row;
        fromSwatch.Click += (_, _) => { _currentRow = row; PickColorInto(fromBox); };
        toSwatch.Click += (_, _) => { _currentRow = row; PickColorInto(toBox); };
        remove.Click += (_, _) => { _rows.Remove(row); _rowsPanel.Children.Remove(element); if (_currentRow == row) _currentRow = null; QueuePreview(); };
        Sync(fromBox, fromSwatch); Sync(toBox, toSwatch);
        _rows.Add(row); _rowsPanel.Children.Add(element);
        _currentRow = row;
        return row;
    }

    static SolidColorBrush ToBrush(IrRgb c) { var (r, g, b) = c.ToRgb8(); return GuiTheme.Brush((byte)r, (byte)g, (byte)b); }

    IEnumerable<(string?, string?, string?)> RowTexts() => _rows.Select(r => ((string?)r.From.Text.Trim(), (string?)r.To.Text.Trim(), (string?)r.Tolerance.Text));

    void PickColorInto(TextBox box)
    {
        var start = IrRgb.TryParseHex(box.Text.Trim(), out var c) ? c : new IrRgb(0, 0, 0);
        if (ChooseColor(start) is { } chosen) box.Text = chosen.ToHex();
    }

    void PickEmfBackground()
    {
        if (ChooseColor(_emfBackground) is not { } chosen) return;
        _emfBackground = chosen; _emfBackgroundSwatch.Background = ToBrush(chosen);
    }

    // Windows の色の選択の画面（WPF には無いので Windows フォームの画面を使う）
    IrRgb? ChooseColor(IrRgb start)
    {
        var (r, g, b) = start.ToRgb8();
        using var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, Color = System.Drawing.Color.FromArgb(r, g, b) };
        if (dialog.ShowDialog(DialogStyle.FormsOwner(this)) != System.Windows.Forms.DialogResult.OK) return null;
        return new IrRgb(dialog.Color.R / 255d, dialog.Color.G / 255d, dialog.Color.B / 255d);
    }

    void CommitPickedColor(IrRgb color)
    {
        if (_previewMode != PreviewMode.Normal) { InvalidatePreviewGeneration(); _previewMode = PreviewMode.Normal; ResetInteraction(); UpdateUiState(); QueuePreview(); }
        var row = _currentRow is { } current && _rows.Contains(current) ? current : AddRow(color.ToHex(), "#000000", "0");
        row.From.Text = color.ToHex();
        ShowStatus($"{color.ToHex()} を置換元に設定しました。");
    }

    void UpdateMatchSummary()
    {
        if (_rows.Count == 0) { _matchSummary.Text = ""; return; }
        IReadOnlyList<ColorReplacement> replacements;
        try { replacements = ColorReplacementRows.Parse(RowTexts(), applyColor: true); }
        catch (ArgumentException ex) { _matchSummary.Text = ex.Message; _matchSummary.Foreground = _palette.Error; return; }
        var matches = ColorTransform.SummarizeMatches(_paletteShown, replacements);
        _matchSummary.Foreground = _palette.Muted;
        _matchSummary.Text = string.Join("　", matches.Select((m, i) => $"{i + 1} 行目に一致 {m.Colors} 色・{m.Count:N0} 件"));
    }

    void RebuildPalette(IReadOnlyList<ColorUse> colors)
    {
        _paletteShown = colors.ToArray();
        _paletteView.Colors = _paletteShown;
        _paletteSummary.Text = $"使用色 全 {colors.Count:N0} 色（多い順）";
    }

    // ---------------- 要求とプレビュー ----------------

    PdfPetaRequest BuildRequest(IReadOnlyList<PdfPetaOutput> outputs, bool applyCrop = true, bool applyColor = true)
    {
        var replacements = ColorReplacementRows.Parse(RowTexts(), applyColor);
        var (rect, margin, cropError) = GuiInputs.ParseCrop(applyCrop && _crop.IsChecked == true,
            _cropValues[0].Text, _cropValues[1].Text, _cropValues[2].Text, _cropValues[3].Text, _margin.Text);
        if (cropError != null) throw new ArgumentException(cropError);
        return new PdfPetaRequest
        {
            InputPdfPath = _pdf, PageNumber = CurrentPage(), KeepBackground = _keepBg.IsChecked == true,
            CropRectMm = rect, CropMarginMm = margin,
            ColorMode = applyColor ? (_mono.IsChecked == true ? ColorMode.Monochrome : _gray.IsChecked == true ? ColorMode.Grayscale : ColorMode.None) : ColorMode.None,
            ColorReplacements = replacements, TextOutline = _outline.IsChecked == true, GroupByLayer = _layers.IsChecked == true,
            EmfBackground = _emfBackground, Outputs = outputs,
        };
    }

    void QueuePreview()
    {
        UpdateMatchSummary();
        _previewCts?.Cancel(); _debounce.Stop(); _debounce.Start();
    }

    async Task RefreshPreviewAsync()
    {
        if (_closing) return;
        if (!File.Exists(_pdf))
        {
            // 開いた PDF が消えた（移動・削除された）。範囲の指定・スポイトの準備のまま止まらないように、モードを通常に戻して知らせる
            if (_previewMode != PreviewMode.Normal) { InvalidatePreviewGeneration(); ResetInteraction(); _previewMode = PreviewMode.Normal; _pickMap = null; UpdateUiState(); }
            if (_pdf.Length > 0) ShowStatus($"PDF が見つかりません: {_pdf}", error: true);
            return;
        }
        int generation = ++_generation;
        _previewCts?.Cancel();
        var previewCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _previewCts = previewCts;
        string path = _session.NewPath(ArtifactKinds.Extension(ArtifactKind.Png), "preview");
        string pickPath = _session.NewPath(ArtifactKinds.Extension(ArtifactKind.Png), "pick");
        var requestedMode = _previewMode;
        IReadOnlyList<ColorUse> inventory = Array.Empty<ColorUse>();
        double pageWidthMm = 0, pageHeightMm = 0, outWidthMm = 0, outHeightMm = 0;
        bool collectColors = requestedMode == PreviewMode.Normal;
        bool pick = PreviewPolicy.NeedsPickMap(requestedMode);
        var flags = PreviewPolicy.RequestFlags(requestedMode);
        string inventoryBoundary = PreviewPolicy.InventoryBoundary(flags.ApplyCrop && _crop.IsChecked == true, _keepBg.IsChecked != true);
        var options = new PdfPetaRunOptions { DiagnosticStageSink = (_, name, ir) =>
        {
            if (name == "acquired") { pageWidthMm = ir.WidthMm; pageHeightMm = ir.HeightMm; }
            outWidthMm = ir.WidthMm; outHeightMm = ir.HeightMm;
            if (collectColors && name == inventoryBoundary) inventory = ColorInventory.Collect(ir);
        }};
        try
        {
            var request = BuildRequest(new[] { new PdfPetaOutput(ArtifactKind.Png, path, PreviewPolicy.PreviewDpi) }, flags.ApplyCrop, flags.ApplyColor);
            int page = request.PageNumber;
            var result = await RunAsync(request, previewCts.Token, options);
            PdfPetaPickMap? pickMap = null;
            if (pick)
                pickMap = await RunPickMapAsync(request with { Outputs = new[] { new PdfPetaOutput(ArtifactKind.Png, pickPath, PreviewPolicy.PreviewDpi) } }, previewCts.Token);
            if (_closing || !PreviewPolicy.CanCommitPreview(generation, _generation, requestedMode, _previewMode)) return;
            var image = LoadBitmap(path);
            var finalMode = PreviewPolicy.ModeAfterPreview(requestedMode);
            _previewMode = finalMode;
            _pickMap = pickMap;
            _snapshot = new PreviewSnapshot(generation, page, new DrawingSize(image.PixelWidth, image.PixelHeight), pageWidthMm, pageHeightMm);
            _preview.Source = image;
            ShowDiagnostics(result);
            if (collectColors && !PreviewPolicy.SameInventory(_paletteShown, inventory)) RebuildPalette(inventory);
            UpdateMatchSummary();
            _previewHost.Cursor = finalMode == PreviewMode.Normal ? null : Cursors.Cross;
            _pageInfo.Text = $"{page} / {Math.Max(_pageCount, 1)} ページ　{pageWidthMm:F1} × {pageHeightMm:F1} mm";
            _sizeInfo.Text = finalMode == PreviewMode.Normal && _crop.IsChecked == true ? $"切り出し {outWidthMm:F1} × {outHeightMm:F1} mm" : "";
            ShowStatus(finalMode switch
            {
                PreviewMode.SelectCrop => "プレビューの上で、切り出す範囲をドラッグしてください（Esc で解除）。",
                PreviewMode.PickColor => "置換元にする線や塗りをクリックしてください（Esc で解除）。",
                _ => "",
            });
            UpdateUiState();
        }
        catch (OperationCanceledException)
        {
            if (PreviewPolicy.EndModeOnCancel(_closing, requestedMode, _previewMode, generation, _generation, _debounce.IsEnabled))
            { _previewMode = PreviewMode.Normal; ResetInteraction(); UpdateUiState(); }
        }
        catch (Exception ex)
        {
            if (!_closing && generation == _generation) { ResetInteraction(); _previewMode = PreviewMode.Normal; UpdateUiState(); ShowStatus($"プレビューを更新できません: {ex.Message}", error: true); }
        }
        finally
        {
            if (ReferenceEquals(_previewCts, previewCts)) _previewCts = null;
            previewCts.Dispose();
            // プレビューの一時ファイルは読み込み済みか、使わないので削除する。消せなくてもセッションの終了で削除される
            foreach (var temp in new[] { path, pickPath }) try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static BitmapImage LoadBitmap(string path)
    {
        var image = new BitmapImage();
        using (var stream = File.OpenRead(path))
        {
            image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit();
        }
        image.Freeze();
        return image;
    }

    async Task ToggleModeAsync(PreviewMode preparing)
    {
        if (_previewMode != PreviewMode.Normal) { CancelMode(); return; }
        if (!File.Exists(_pdf)) { ShowStatus("先に PDF を開いてください。", error: true); return; }
        // 予約済みの作り直しは、このモードのプレビューで置き換える
        _debounce.Stop();
        _previewMode = preparing; ResetInteraction(); UpdateUiState();
        ShowStatus(preparing == PreviewMode.PreparingCrop ? "ページ全体のプレビューを作っています…" : "元の色のプレビューを作っています…");
        await RefreshPreviewAsync();
    }

    void CancelMode(bool rerender = true)
    {
        InvalidatePreviewGeneration(); ResetInteraction(); _previewMode = PreviewMode.Normal; _pickMap = null; UpdateUiState(); ShowStatus("");
        if (rerender && File.Exists(_pdf)) _ = RefreshPreviewAsync();
    }

    void InvalidatePreviewGeneration() { ++_generation; _previewCts?.Cancel(); }

    void ResetInteraction()
    {
        _previewHost.ReleaseMouseCapture(); _previewHost.Cursor = null;
        _dragStartImage = _dragCurrentImage = null; DrawSelection();
    }

    DrawingSize HostSize => new((int)_previewHost.ActualWidth, (int)_previewHost.ActualHeight);

    static DrawingPoint ToDrawing(Point p) => new((float)p.X, (float)p.Y);

    void OnPreviewHostMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_snapshot is null || _snapshot.Generation != _generation) return;
        if (!PreviewGeometry.TryControlToImagePixel(ToDrawing(e.GetPosition(_previewHost)), HostSize, _snapshot.ImageSize, out var imagePoint)) return;
        if (_previewMode == PreviewMode.SelectCrop) { _dragStartImage = _dragCurrentImage = imagePoint; _previewHost.CaptureMouse(); DrawSelection(); }
        else if (_previewMode == PreviewMode.PickColor) PickPreviewColor(imagePoint);
    }

    void OnPreviewHostMouseMove(object sender, MouseEventArgs e)
    {
        if (_previewMode != PreviewMode.SelectCrop || _dragStartImage is null || _snapshot is null) return;
        _dragCurrentImage = PreviewGeometry.ControlToClampedImagePixel(ToDrawing(e.GetPosition(_previewHost)), HostSize, _snapshot.ImageSize);
        DrawSelection();
    }

    void OnPreviewHostMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_previewMode != PreviewMode.SelectCrop || _dragStartImage is not { } start || _snapshot is null) return;
        var end = PreviewGeometry.ControlToClampedImagePixel(ToDrawing(e.GetPosition(_previewHost)), HostSize, _snapshot.ImageSize);
        ResetInteraction();
        var rect = PreviewGeometry.ImageSelectionToControl(start, end, HostSize, _snapshot.ImageSize);
        if (rect.Width < MinCropDragLogicalPx || rect.Height < MinCropDragLogicalPx) { ShowStatus("範囲が小さすぎます。ドラッグして指定してください。", error: true); return; }
        var a = PreviewGeometry.ImageToPageMm(start, _snapshot.ImageSize, _snapshot.PageWidthMm, _snapshot.PageHeightMm);
        var b = PreviewGeometry.ImageToPageMm(end, _snapshot.ImageSize, _snapshot.PageWidthMm, _snapshot.PageHeightMm);
        var values = new[] { Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y) };
        for (int i = 0; i < 4; i++) _cropValues[i].Text = Math.Round(values[i], 2).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        _margin.Text = "0"; _crop.IsChecked = true; _previewMode = PreviewMode.Normal; UpdateUiState();
        ShowStatus("ドラッグした範囲で切り出し、余白を 0 にしました。"); QueuePreview();
    }

    void DrawSelection()
    {
        if (_dragStartImage is not { } a || _dragCurrentImage is not { } b || _snapshot is null) { _selection.Visibility = Visibility.Collapsed; return; }
        var rect = PreviewGeometry.ImageSelectionToControl(a, b, HostSize, _snapshot.ImageSize);
        Canvas.SetLeft(_selection, rect.X); Canvas.SetTop(_selection, rect.Y);
        _selection.Width = rect.Width; _selection.Height = rect.Height; _selection.Visibility = Visibility.Visible;
    }

    void PickPreviewColor(DrawingPoint imagePoint)
    {
        if (_pickMap is not { } map || _snapshot is null) { ShowStatus("図面の色を取得できません。", error: true); return; }
        // 色番号の画像はプレビューと同じ要求・解像度で描くが、念のため大きさの比で位置を合わせる
        var pixel = PreviewGeometry.PixelIndex(new DrawingPoint(imagePoint.X * map.Width / _snapshot.ImageSize.Width,
            imagePoint.Y * map.Height / _snapshot.ImageSize.Height), new DrawingSize(map.Width, map.Height));
        int radius = PreviewGeometry.ControlLengthToImagePixels(PickRadiusLogicalPx, HostSize, new DrawingSize(map.Width, map.Height));
        if (map.ColorAt(pixel.X, pixel.Y, radius) is not { } color) { ShowStatus("クリックした位置に線や塗りがありません（画像は色置換の対象外です）。", error: true); return; }
        _pickMap = null;
        CommitPickedColor(color);
    }

    // ---------------- 保存とコピー ----------------

    async Task CopyAsync()
    {
        string path = _session.NewPath(ArtifactKinds.Extension(ArtifactKind.Emf), Path.GetFileNameWithoutExtension(_pdf));
        try
        {
            var result = await RunAsync(BuildRequest(new[] { new PdfPetaOutput(ArtifactKind.Emf, path, ToClipboard: true) }), _shutdownCts.Token);
            if (_closing) return;
            ShowDiagnostics(result);
            if (result.ClipboardError is null) ShowStatus("クリップボードにコピーしました。Word・Excel などに貼り付けられます。");
            else
            {
                ShowStatus("クリップボードにコピーできませんでした。", error: true);
                MessageBox.Show(this, $"クリップボードにコピーできませんでした。次の EMF を「挿入 > 図」から使えます。\n{result.ClipboardEmfPath}\n\n{result.ClipboardError}",
                    "CADPeta", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    async Task SaveAsync()
    {
        var kinds = SaveFormats.Where(k => _formats[k].IsChecked == true).ToArray();
        if (kinds.Length == 0) { ShowStatus("保存する形式を 1 つ以上選んでください。", error: true); return; }
        var (dpi, dpiError) = GuiInputs.PngDpiFor(kinds, _dpi.Text);
        if (dpiError != null) { ShowStatus(dpiError, error: true); return; }
        string defaultName = GuiInputs.DefaultFileName(_pdf, CurrentPage(), _pageCount);
        string folder = Path.GetDirectoryName(_pdf) ?? "";
        var dialog = kinds.Length == 1
            ? new Microsoft.Win32.SaveFileDialog { FileName = defaultName, InitialDirectory = folder, AddExtension = true, OverwritePrompt = true,
                Filter = $"{ArtifactKinds.Label(kinds[0])} (*{ArtifactKinds.Extension(kinds[0])})|*{ArtifactKinds.Extension(kinds[0])}" }
            : new Microsoft.Win32.SaveFileDialog { FileName = defaultName, InitialDirectory = folder, Title = "保存先と基本のファイル名", Filter = "基本のファイル名|*.*",
                AddExtension = false, OverwritePrompt = false };
        if (dialog.ShowDialog(this) != true) return;
        string chosen = kinds.Length == 1 ? dialog.FileName
            : Path.Combine(Path.GetDirectoryName(dialog.FileName)!, Path.GetFileNameWithoutExtension(dialog.FileName));
        var outputs = GuiInputs.SaveTargets(kinds, chosen, dpi);
        var existing = kinds.Length == 1 ? Array.Empty<string>() : outputs.Where(o => File.Exists(o.Path)).Select(o => o.Path).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this, "次のファイルを上書きします。\n" + string.Join("\n", existing), "CADPeta",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            var result = await RunAsync(BuildRequest(outputs), _shutdownCts.Token);
            if (_closing) return;
            ShowDiagnostics(result);
            ShowStatus(outputs.Count == 1 ? $"保存しました: {outputs[0].Path}" : $"{outputs.Count} 形式を保存しました: {chosen}.*");
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    async Task<PdfPetaResult> RunAsync(PdfPetaRequest request, CancellationToken ct, PdfPetaRunOptions? options = null)
    {
        var errors = PdfPetaGuiPresenter.ValidateForGui(request);
        if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        await _runGate.WaitAsync(ct);
        SetBusy(true);
        try { var writer = new ProgressWriter(AppendLog); return await Task.Run(() => PdfPetaPipeline.Run(request, writer, options, ct), ct); }
        finally { SetBusy(false); _runGate.Release(); }
    }

    async Task<PdfPetaPickMap> RunPickMapAsync(PdfPetaRequest request, CancellationToken ct)
    {
        await _runGate.WaitAsync(ct);
        SetBusy(true);
        try { var writer = new ProgressWriter(AppendLog); return await Task.Run(() => PdfPetaPipeline.RenderPickMap(request, writer, ct), ct); }
        finally { SetBusy(false); _runGate.Release(); }
    }

    void SetBusy(bool busy)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => SetBusy(busy)); return; }
        _busy = busy; UpdateUiState();
        if (busy) ShowStatus("処理中…");
        else if (_status.Text == "処理中…") ShowStatus("");
    }

    void UpdateUiState()
    {
        bool save = PreviewPolicy.CanSave(_busy, _previewMode, _closing) && File.Exists(_pdf);
        _copy.IsEnabled = _save.IsEnabled = save;
        bool modeButtons = !_closing && (_previewMode != PreviewMode.Normal || (!_busy && File.Exists(_pdf)));
        _selectCrop.IsEnabled = modeButtons && _previewMode is PreviewMode.Normal or PreviewMode.PreparingCrop or PreviewMode.SelectCrop;
        _pickColor.IsEnabled = modeButtons && _previewMode is PreviewMode.Normal or PreviewMode.PreparingPickColor or PreviewMode.PickColor;
    }

    void ShowDiagnostics(PdfPetaResult result)
    {
        _diagnostics.Children.Clear();
        foreach (var d in result.Diagnostics)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var count = new TextBlock { Text = $"{d.Count:N0} 件", TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
            var targets = new TextBlock { Text = string.Join(", ", d.Targets), Foreground = _palette.Muted, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join("\n", d.Targets) };
            Grid.SetColumn(count, 1); Grid.SetColumn(targets, 2);
            row.Children.Add(new TextBlock { Text = d.Type, TextWrapping = TextWrapping.Wrap }); row.Children.Add(count); row.Children.Add(targets);
            _diagnostics.Children.Add(row);
        }
        if (result.Diagnostics.Count == 0) _diagnostics.Children.Add(_theme.Muted("記録はありません。"));
        _diagnosticsTab.Content = result.Diagnostics.Count > 0 ? $"診断 {result.Diagnostics.Count}" : "診断";
        int removed = result.Diagnostics.Where(d => d.Type.StartsWith("背景矩形の除去", StringComparison.Ordinal)).Sum(d => d.Count);
        _backgroundInfo.Text = removed > 0 ? $"背景除去 {removed} 件" : "";
    }

    void ShowStatus(string text, bool error = false) { _status.Text = text; _status.Foreground = error ? _palette.Error : _palette.Text; }

    void ShowError(Exception ex)
    {
        ShowStatus(ex.Message, error: true);
        MessageBox.Show(this, ex.Message, "CADPeta", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void AppendLog(string line)
    {
        if (_closing) return;
        Dispatcher.BeginInvoke(() => { if (!_closing) _log.AppendText(line + Environment.NewLine); });
    }

    // ---------------- 終了 ----------------

    async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closing) { e.Cancel = ShutdownClosePolicy.ShouldCancel(_closing, _shutdownReady); return; }
        _closing = true; ResetInteraction(); UpdateUiState(); _debounce.Stop(); _previewCts?.Cancel(); _shutdownCts.Cancel();
        if (_runGate.CurrentCount == 0)
        {
            e.Cancel = true; ShowStatus("処理を安全に終了しています…");
            await _runGate.WaitAsync(); _runGate.Release();
            _shutdownReady = true;
            Close();
        }
        else _shutdownReady = true;
    }

    void DisposeResources()
    {
        _previewCts?.Cancel(); _previewCts?.Dispose(); _previewCts = null;
        _debounce.Stop(); _runGate.Dispose(); _shutdownCts.Dispose(); _session.Dispose();
    }

    sealed class ProgressWriter(Action<string> report) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => report(value ?? "");
    }

    sealed record PreviewSnapshot(int Generation, int Page, DrawingSize ImageSize, double PageWidthMm, double PageHeightMm);
}

internal static class ShutdownClosePolicy
{
    public static bool ShouldCancel(bool closing, bool ready) => closing && !ready;
}
