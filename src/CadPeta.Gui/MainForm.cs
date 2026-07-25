using System.Drawing;
using CadPeta.App;
using CadPeta.Geometry;
using CadPeta.Ir;
using CadPeta.Transforms;

namespace CadPeta.Gui;

public sealed class MainForm : Form
{
    readonly TextBox _input = new() { Dock = DockStyle.Fill, PlaceholderText = "PDFをドロップするか参照…" };
    readonly RadioButton _none = new() { Text = "そのまま", Checked = true, AutoSize = true };
    readonly RadioButton _mono = new() { Text = "モノクロ", AutoSize = true };
    readonly RadioButton _gray = new() { Text = "グレースケール", AutoSize = true };
    readonly CheckBox _keepBg = new() { Text = "背景を残す", AutoSize = true };
    readonly CheckBox _outline = new() { Text = "文字をアウトライン化", AutoSize = true };
    readonly CheckBox _layers = new() { Text = "レイヤ別にグループ化", AutoSize = true };
    readonly CheckBox _crop = new() { Text = "クロップ (mm)", AutoSize = true };
    readonly NumericUpDown[] _cropValues = Enumerable.Range(0, 4).Select(_ => Num(-100000, 100000, 0, 2)).ToArray();
    readonly NumericUpDown _margin = Num(0, 10000, 0, 2);
    readonly NumericUpDown _dpi = Num(1, 2400, (decimal)PdfPetaDefaults.PngDpi, 0);
    readonly CheckedListBox _formats = new() { Width = 250, Height = 72, CheckOnClick = true };
    IrRgb _emfBackground = new(1, 1, 1);
    readonly DataGridView _colors = new() { Dock = DockStyle.Fill, AllowUserToAddRows = false, AutoGenerateColumns = false };
    readonly PictureBox _preview = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.WhiteSmoke };
    readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both };
    readonly ListView _diagnostics = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
    readonly Label _status = new() { AutoSize = true, ForeColor = Color.Firebrick };
    readonly Button _paste = new() { Text = "Office に貼る", AutoSize = true };
    readonly Button _save = new() { Text = "ファイルに保存…", AutoSize = true };
    readonly Button _saveMulti = new() { Text = "複数形式で保存…", AutoSize = true };
    readonly Button _refresh = new() { Text = "プレビュー更新", AutoSize = true };
    readonly Button _selectCrop = new() { Text = "範囲をドラッグ指定", AutoSize = true };
    readonly Button _pickColor = new() { Text = "スポイト", AutoSize = true };
    readonly FlowLayoutPanel _palettePanel = new() { Width = 390, Height = 72, AutoScroll = true };
    readonly Label _paletteSummary = new() { AutoSize = true, Text = "使用色: プレビュー生成後に表示（件数=属性数）" };
    readonly ToolTip _paletteTips = new();
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 300 };
    readonly SemaphoreSlim _runGate = new(1, 1);
    readonly GuiSession _session = new();
    readonly CancellationTokenSource _shutdownCts = new();
    CancellationTokenSource? _previewCts;
    int _generation;
    bool _closing;
    bool _shutdownReady;
    bool _busy;
    PreviewMode _previewMode;
    PointF? _dragStartImage, _dragCurrentImage;
    PreviewSnapshot? _snapshot;
    IReadOnlyList<ColorUse> _paletteShown = Array.Empty<ColorUse>();
    Icon? _appIcon;
    const int MaxPaletteSwatches = 64;
    const int MaxEyedropperSnapDistance = 48;
    const double MinCropDragLogicalPx = 4.0;
    const double LogicalDpiBase = 96.0;
    const int CropOverlayAlpha = 48;
    const int PaletteSwatchWidth = 28;
    const int PaletteSwatchHeight = 24;

    public MainForm()
    {
        Text = "CADPeta PDF"; Width = 1180; Height = 800; MinimumSize = new Size(850, 600);
        AllowDrop = true; AutoScaleMode = AutoScaleMode.Dpi; KeyPreview = true;
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("CadPeta.Gui.app.ico");
            if (stream is not null) { using var icon = new Icon(stream); _appIcon = (Icon)icon.Clone(); Icon = _appIcon; }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or OutOfMemoryException
            or System.Runtime.InteropServices.ExternalException)
        {
            System.Diagnostics.Debug.WriteLine($"アプリアイコンを読み込めないため既定アイコンを使用: {ex.Message}");
        }
        _colors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "変換前 #RRGGBB", DataPropertyName = "From", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _colors.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "変換後 #RRGGBB", DataPropertyName = "To", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _diagnostics.Columns.Add("種別", 360); _diagnostics.Columns.Add("件数", 70); _diagnostics.Columns.Add("対象", 500);
        _formats.Items.AddRange(new object[] { "EMF", "SVG", "PDF", "PNG" });
        Controls.Add(BuildLayout());
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) => { if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) SelectInput(f[0]); };
        _paste.Click += async (_, _) => await PasteAsync(); _save.Click += async (_, _) => await SaveAsync();
        _saveMulti.Click += async (_, _) => await SaveMultipleAsync();
        _refresh.Click += async (_, _) => await RefreshPreviewAsync();
        _selectCrop.Click += async (_, _) => await ToggleModeAsync(PreviewMode.PreparingCrop);
        _pickColor.Click += async (_, _) => await ToggleModeAsync(PreviewMode.PreparingPickColor);
        _debounce.Tick += async (_, _) => { _debounce.Stop(); await RefreshPreviewAsync(); };
        foreach (var radio in new[] { _none, _mono, _gray }) radio.CheckedChanged += (_, _) => QueuePreview();
        foreach (var check in new[] { _keepBg, _outline, _layers, _crop }) check.CheckedChanged += (_, _) => QueuePreview();
        foreach (var number in _cropValues.Append(_margin)) number.ValueChanged += (_, _) => QueuePreview();
        _colors.CellValueChanged += (_, _) => QueuePreview();
        _preview.MouseDown += PreviewMouseDown; _preview.MouseMove += PreviewMouseMove;
        _preview.MouseUp += PreviewMouseUp; _preview.Paint += PreviewPaint;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape && _previewMode != PreviewMode.Normal) { CancelMode(); e.Handled = true; } };
        Deactivate += (_, _) => { if (_previewMode != PreviewMode.Normal) CancelMode(); };
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => DisposeResources();
        UpdateUiState();
    }

    Control BuildLayout()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 420)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var left = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
        var inputRow = new TableLayoutPanel { Width = 390, Height = 32, ColumnCount = 2 }; inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var browse = new Button { Text = "参照…", AutoSize = true }; browse.Click += (_, _) => Browse(); inputRow.Controls.Add(_input); inputRow.Controls.Add(browse, 1, 0);
        left.Controls.Add(new Label { Text = "入力PDF（ウィンドウへドロップ可）", AutoSize = true }); left.Controls.Add(inputRow);
        left.Controls.Add(Flow(_none, _mono, _gray)); left.Controls.Add(Flow(_keepBg, _outline, _layers));
        left.Controls.Add(Flow(_crop, new Label { Text = "x0" }, _cropValues[0], new Label { Text = "y0" }, _cropValues[1]));
        left.Controls.Add(Flow(new Label { Text = "x1" }, _cropValues[2], new Label { Text = "y1" }, _cropValues[3], new Label { Text = "余白" }, _margin));
        left.Controls.Add(_selectCrop);
        left.Controls.Add(new Label { Text = "色置換（セルをダブルクリックで色選択）", AutoSize = true });
        var colorPanel = new Panel { Width = 390, Height = 150 }; colorPanel.Controls.Add(_colors); left.Controls.Add(colorPanel);
        var add = new Button { Text = "色置換を追加" }; var remove = new Button { Text = "選択行を削除" };
        add.Click += (_, _) => _colors.Rows.Add("#000000", "#000000"); remove.Click += (_, _) => { foreach (DataGridViewRow r in _colors.SelectedRows) if (!r.IsNewRow) _colors.Rows.Remove(r); };
        _colors.CellDoubleClick += (_, e) => PickGridColor(e.RowIndex, e.ColumnIndex); left.Controls.Add(Flow(add, remove, _pickColor));
        left.Controls.Add(_paletteSummary); left.Controls.Add(_palettePanel);
        var bg = new Button { Text = "EMF背景色…", AutoSize = true }; bg.Click += (_, _) => PickEmfBackground(bg);
        left.Controls.Add(Flow(new Label { Text = "PNG DPI" }, _dpi, bg));
        left.Controls.Add(new Label { Text = "複数保存する形式", AutoSize = true }); left.Controls.Add(_formats);
        left.Controls.Add(Flow(_paste, _save, _saveMulti, _refresh)); left.Controls.Add(_status);
        var tabs = new TabControl { Dock = DockStyle.Fill }; tabs.TabPages.Add(new TabPage("プレビュー") { Controls = { _preview } }); tabs.TabPages.Add(new TabPage("診断") { Controls = { _diagnostics } }); tabs.TabPages.Add(new TabPage("ログ") { Controls = { _log } });
        root.Controls.Add(left); root.Controls.Add(tabs, 1, 0); return root;
    }

    static FlowLayoutPanel Flow(params Control[] controls) { var p = new FlowLayoutPanel { AutoSize = true, Width = 400 }; p.Controls.AddRange(controls); return p; }
    static NumericUpDown Num(decimal min, decimal max, decimal value, int decimals) => new() { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 70 };
    void Browse() { using var d = new OpenFileDialog { Filter = "PDF (*.pdf)|*.pdf" }; if (d.ShowDialog(this) == DialogResult.OK) SelectInput(d.FileName); }
    void SelectInput(string path) { if (!path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) { _status.Text = "PDFを指定してください"; return; } if (_previewMode != PreviewMode.Normal) CancelMode(false); _input.Text = path; if (new FileInfo(path).Length > PdfPetaGuiPresenter.LargePdfWarnBytes && MessageBox.Show(this, "大きなPDFです。プレビューを生成しますか？", "確認", MessageBoxButtons.YesNo) != DialogResult.Yes) return; QueuePreview(); }
    void QueuePreview() { _previewCts?.Cancel(); _debounce.Stop(); _debounce.Start(); }
    void PickGridColor(int row, int col) { if (row < 0 || col < 0) return; using var d = new ColorDialog(); if (d.ShowDialog(this) == DialogResult.OK) _colors.Rows[row].Cells[col].Value = $"#{d.Color.R:X2}{d.Color.G:X2}{d.Color.B:X2}"; }
    void PickEmfBackground(Button button) { using var d = new ColorDialog { Color = Color.FromArgb((int)Math.Round(_emfBackground.R * 255), (int)Math.Round(_emfBackground.G * 255), (int)Math.Round(_emfBackground.B * 255)) }; if (d.ShowDialog(this) != DialogResult.OK) return; _emfBackground = new IrRgb(d.Color.R / 255d, d.Color.G / 255d, d.Color.B / 255d); button.BackColor = d.Color; }

    PdfPetaRequest BuildRequest(IReadOnlyList<PdfPetaOutput> outputs) => BuildRequestCore(outputs, true, true);
    PdfPetaRequest BuildPreviewRequest(string path, PreviewMode mode)
    {
        var flags = PreviewPolicy.RequestFlags(mode);
        return BuildRequestCore(new[] { new PdfPetaOutput(PdfPetaOutputKind.Png, path, PdfPetaDefaults.PngDpi) }, flags.ApplyCrop, flags.ApplyColor);
    }

    PdfPetaRequest BuildRequestCore(IReadOnlyList<PdfPetaOutput> outputs, bool applyCrop, bool applyColor)
    {
        var replacements = new List<ColorReplacement>();
        foreach (DataGridViewRow row in _colors.Rows)
        {
            string? fromText = row.Cells[0].Value?.ToString(), toText = row.Cells[1].Value?.ToString();
            if (!IrRgb.TryParseHex(fromText, out var from) || !IrRgb.TryParseHex(toText, out var to))
                throw new ArgumentException($"色置換は #RRGGBB 形式で指定してください: {fromText} → {toText}");
            replacements.Add(new(from, to));
        }
        return new PdfPetaRequest { InputPdfPath = _input.Text, KeepBackground = _keepBg.Checked,
            CropRectMm = applyCrop && _crop.Checked ? new RectMm((double)_cropValues[0].Value, (double)_cropValues[1].Value, (double)_cropValues[2].Value, (double)_cropValues[3].Value) : null,
            CropMarginMm = applyCrop && _crop.Checked ? (double)_margin.Value : 0, ColorMode = applyColor ? (_mono.Checked ? ColorMode.Monochrome : _gray.Checked ? ColorMode.Grayscale : ColorMode.None) : ColorMode.None,
            ColorReplacements = applyColor ? replacements : Array.Empty<ColorReplacement>(), TextOutline = _outline.Checked, GroupByLayer = _layers.Checked,
            EmfBackground = _emfBackground, Outputs = outputs };
    }

    async Task RefreshPreviewAsync()
    {
        if (_closing || !File.Exists(_input.Text)) return;
        int generation = ++_generation;
        _previewCts?.Cancel();
        var previewCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
        _previewCts = previewCts;
        string path = _session.NewPath(".png", "preview");
        var requestedMode = _previewMode;
        IReadOnlyList<ColorUse> inventory = Array.Empty<ColorUse>();
        double pageWidthMm = 0, pageHeightMm = 0;
        bool collectColors = requestedMode is PreviewMode.Normal or PreviewMode.PreparingPickColor;
        var requestFlags = PreviewPolicy.RequestFlags(requestedMode);
        string inventoryBoundary = PreviewPolicy.InventoryBoundary(requestFlags.ApplyCrop && _crop.Checked, !_keepBg.Checked);
        var options = new PdfPetaRunOptions { DiagnosticStageSink = (_, name, ir) =>
        {
            if (name == "acquired") { pageWidthMm = ir.WidthMm; pageHeightMm = ir.HeightMm; }
            if (collectColors && name == inventoryBoundary) inventory = ColorInventory.Collect(ir);
        }};
        try
        {
            var result = await RunAsync(BuildPreviewRequest(path, requestedMode), previewCts.Token, options);
            if (_closing || !PreviewPolicy.CanCommitPreview(generation, _generation, requestedMode, _previewMode)) return;
            using var s = File.OpenRead(path); using var loaded = Image.FromStream(s); var image = new Bitmap(loaded);
            var finalMode = requestedMode switch { PreviewMode.PreparingCrop => PreviewMode.SelectCrop, PreviewMode.PreparingPickColor => PreviewMode.PickColor, _ => PreviewMode.Normal };
            _previewMode = finalMode;
            _snapshot = new PreviewSnapshot(generation, finalMode, image.Size, pageWidthMm, pageHeightMm, inventory);
            var old = _preview.Image; _preview.Image = image; old?.Dispose(); ShowDiagnostics(result);
            if (collectColors && !PreviewPolicy.SameInventory(_paletteShown, inventory)) RebuildPalette(inventory);
            _preview.Cursor = finalMode == PreviewMode.Normal ? Cursors.Default : Cursors.Cross;
            _status.Text = finalMode switch { PreviewMode.SelectCrop => "プレビュー上で切り出す範囲をドラッグしてください（Escで解除）", PreviewMode.PickColor => "置換元にする線や塗りをクリックしてください（Escで解除）", _ => "" };
            UpdateUiState();
        }
        catch (OperationCanceledException)
        {
            if (!_closing && requestedMode is (PreviewMode.PreparingCrop or PreviewMode.PreparingPickColor)
                && _previewMode == requestedMode)
            { _previewMode = PreviewMode.Normal; ResetInteraction(); UpdateUiState(); }
        }
        catch (Exception ex) { if (!_closing && generation == _generation) { ResetInteraction(); _previewMode = PreviewMode.Normal; UpdateUiState(); ShowPreviewError(ex); } }
        finally
        {
            if (ReferenceEquals(_previewCts, previewCts)) _previewCts = null;
            previewCts.Dispose();
            try { File.Delete(path); } catch { }
        }
    }

    async Task ToggleModeAsync(PreviewMode preparing)
    {
        if (_previewMode != PreviewMode.Normal) { CancelMode(); return; }
        if (!File.Exists(_input.Text)) { _status.Text = "先にPDFを読み込んでください。"; return; }
        _previewMode = preparing; ResetInteraction(); UpdateUiState();
        _status.Text = preparing == PreviewMode.PreparingCrop ? "全面プレビュー生成中…" : "実色プレビュー生成中…";
        await RefreshPreviewAsync();
    }

    void CancelMode(bool rerender = true)
    {
        InvalidatePreviewGeneration(); ResetInteraction(); _previewMode = PreviewMode.Normal; UpdateUiState();
        if (rerender && File.Exists(_input.Text)) _ = RefreshPreviewAsync();
    }

    void InvalidatePreviewGeneration() { ++_generation; _previewCts?.Cancel(); }

    void ResetInteraction() { _preview.Capture = false; _preview.Cursor = Cursors.Default; _dragStartImage = _dragCurrentImage = null; _preview.Invalidate(); }

    void PreviewMouseDown(object? sender, MouseEventArgs e)
    {
        if (_snapshot is null || _snapshot.Generation != _generation || e.Button != MouseButtons.Left) return;
        if (!PreviewGeometry.TryControlToImagePixel(e.Location, _preview.ClientSize, _snapshot.ImageSize, out var imagePoint)) return;
        if (_previewMode == PreviewMode.SelectCrop) { _dragStartImage = _dragCurrentImage = imagePoint; _preview.Capture = true; }
        else if (_previewMode == PreviewMode.PickColor) PickPreviewColor(imagePoint);
    }

    void PreviewMouseMove(object? sender, MouseEventArgs e)
    {
        if (_previewMode != PreviewMode.SelectCrop || _dragStartImage is null || _snapshot is null) return;
        _dragCurrentImage = PreviewGeometry.ControlToClampedImagePixel(e.Location, _preview.ClientSize, _snapshot.ImageSize); _preview.Invalidate();
    }

    void PreviewMouseUp(object? sender, MouseEventArgs e)
    {
        if (_previewMode != PreviewMode.SelectCrop || _dragStartImage is not { } start || _snapshot is null) return;
        var end = PreviewGeometry.ControlToClampedImagePixel(e.Location, _preview.ClientSize, _snapshot.ImageSize); ResetInteraction();
        double threshold = MinCropDragLogicalPx * _preview.DeviceDpi / LogicalDpiBase;
        var rect = PreviewGeometry.ImageSelectionToControl(start, end, _preview.ClientSize, _snapshot.ImageSize);
        if (rect.Width < threshold || rect.Height < threshold) { _status.Text = "範囲が小さすぎます。ドラッグして指定してください。"; return; }
        var a = PreviewGeometry.ImageToPageMm(start, _snapshot.ImageSize, _snapshot.PageWidthMm, _snapshot.PageHeightMm);
        var b = PreviewGeometry.ImageToPageMm(end, _snapshot.ImageSize, _snapshot.PageWidthMm, _snapshot.PageHeightMm);
        SetNumeric(_cropValues[0], (decimal)Math.Min(a.X, b.X)); SetNumeric(_cropValues[1], (decimal)Math.Min(a.Y, b.Y));
        SetNumeric(_cropValues[2], (decimal)Math.Max(a.X, b.X)); SetNumeric(_cropValues[3], (decimal)Math.Max(a.Y, b.Y));
        _margin.Value = 0; _crop.Checked = true; _previewMode = PreviewMode.Normal; UpdateUiState();
        _status.Text = "ドラッグ範囲で確定し、余白を0にしました。"; QueuePreview();
    }

    void PreviewPaint(object? sender, PaintEventArgs e)
    {
        if (_dragStartImage is not { } a || _dragCurrentImage is not { } b || _snapshot is null) return;
        var rect = PreviewGeometry.ImageSelectionToControl(a, b, _preview.ClientSize, _snapshot.ImageSize);
        using var brush = new SolidBrush(Color.FromArgb(CropOverlayAlpha, Color.DodgerBlue)); using var pen = new Pen(Color.DodgerBlue, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
        e.Graphics.FillRectangle(brush, rect); e.Graphics.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
    }

    void PickPreviewColor(PointF imagePoint)
    {
        if (_preview.Image is not Bitmap bitmap || _snapshot is null || _snapshot.Inventory.Count == 0) { _status.Text = "図面色を取得できません。"; return; }
        var pixel = PreviewGeometry.PixelIndex(imagePoint, bitmap.Size); var c = bitmap.GetPixel(pixel.X, pixel.Y);
        ColorUse? best = null; int bestDistance = int.MaxValue;
        foreach (var use in _snapshot.Inventory) { var q = use.Color.ToRgb8(); int d = Sq(c.R-q.R)+Sq(c.G-q.G)+Sq(c.B-q.B); if (d < bestDistance) { bestDistance=d; best=use; } }
        if (best is null || bestDistance > MaxEyedropperSnapDistance * MaxEyedropperSnapDistance) { _status.Text = "図面色を取得できません。"; return; }
        CommitPickedColor(best.Value.Color);
    }
    static int Sq(int x) => x*x;

    void SetReplacementFrom(IrRgb color)
    {
        int row = _colors.CurrentRow?.Index ?? -1; if (row < 0) row = _colors.Rows.Add(color.ToHex(), "#000000");
        _colors.ClearSelection(); _colors.Rows[row].Selected = true; _colors.CurrentCell = _colors.Rows[row].Cells[0]; _colors.Rows[row].Cells[0].Value = color.ToHex();
    }

    void CommitPickedColor(IrRgb color)
    {
        if (_previewMode != PreviewMode.Normal) { InvalidatePreviewGeneration(); _previewMode = PreviewMode.Normal; ResetInteraction(); UpdateUiState(); }
        SetReplacementFrom(color); _status.Text = $"{color.ToHex()} を置換元へ設定しました。";
    }

    void RebuildPalette(IReadOnlyList<ColorUse> colors)
    {
        foreach (Control control in _palettePanel.Controls.Cast<Control>().ToArray()) { _paletteTips.SetToolTip(control, null); control.Dispose(); }
        _palettePanel.Controls.Clear(); _paletteShown = colors.ToArray(); int shown = Math.Min(colors.Count, MaxPaletteSwatches); _paletteSummary.Text = $"使用色: 全{colors.Count}色中 上位{shown}色（件数=属性数）";
        foreach (var use in colors.Take(shown)) { var q=use.Color.ToRgb8(); var button=new Button { Width=PaletteSwatchWidth, Height=PaletteSwatchHeight, BackColor=Color.FromArgb(q.R,q.G,q.B), FlatStyle=FlatStyle.Flat, AccessibleName=$"{use.Color.ToHex()} {use.Count}件" }; button.Click += (_,_) => CommitPickedColor(use.Color); _paletteTips.SetToolTip(button,$"{use.Color.ToHex()}（{use.Count}件）"); _palettePanel.Controls.Add(button); }
    }

    static void SetNumeric(NumericUpDown control, decimal value) => control.Value = Math.Clamp(Math.Round(value, control.DecimalPlaces), control.Minimum, control.Maximum);

    async Task PasteAsync()
    {
        string path = _session.NewPath(".emf", Path.GetFileNameWithoutExtension(_input.Text));
        try { var result = await RunAsync(BuildRequest(new[] { new PdfPetaOutput(PdfPetaOutputKind.Clipboard, path) }), _shutdownCts.Token); if (_closing) return; ShowDiagnostics(result); if (result.ClipboardError is null) MessageBox.Show(this, "Officeへ貼り付けできます。", "完了"); else MessageBox.Show(this, $"クリップボード登録に失敗しました。\nEMF: {result.ClipboardEmfPath}\n{result.ClipboardError}", "代替ファイルを利用してください", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        catch (OperationCanceledException) when (_closing) { } catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    async Task SaveAsync()
    {
        using var choose = new SaveFileDialog { Filter = "EMF (*.emf)|*.emf|SVG (*.svg)|*.svg|PDF (*.pdf)|*.pdf|PNG (*.png)|*.png", AddExtension = true, OverwritePrompt = true };
        if (choose.ShowDialog(this) != DialogResult.OK) return;
        var kind = choose.FilterIndex switch { 1 => PdfPetaOutputKind.Emf, 2 => PdfPetaOutputKind.Svg, 3 => PdfPetaOutputKind.Pdf, _ => PdfPetaOutputKind.Png };
        try { var result = await RunAsync(BuildRequest(new[] { new PdfPetaOutput(kind, choose.FileName, kind == PdfPetaOutputKind.Png ? (double)_dpi.Value : null) }), _shutdownCts.Token); if (_closing) return; ShowDiagnostics(result); MessageBox.Show(this, $"保存しました。\n{choose.FileName}", "完了"); }
        catch (OperationCanceledException) when (_closing) { } catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    async Task SaveMultipleAsync()
    {
        if (_formats.CheckedItems.Count == 0) { MessageBox.Show(this, "保存する形式を1つ以上選択してください。"); return; }
        using var choose = new SaveFileDialog { Title = "保存先と基本ファイル名を指定", Filter = "基本ファイル名|*.*", AddExtension = false,
            FileName = Path.GetFileNameWithoutExtension(_input.Text) };
        if (choose.ShowDialog(this) != DialogResult.OK) return;
        string dir = Path.GetDirectoryName(choose.FileName)!; string baseName = Path.GetFileNameWithoutExtension(choose.FileName);
        var outputs = new List<PdfPetaOutput>();
        foreach (string format in _formats.CheckedItems)
        {
            var (kind, ext) = format switch { "EMF" => (PdfPetaOutputKind.Emf, ".emf"), "SVG" => (PdfPetaOutputKind.Svg, ".svg"), "PDF" => (PdfPetaOutputKind.Pdf, ".pdf"), _ => (PdfPetaOutputKind.Png, ".png") };
            outputs.Add(new PdfPetaOutput(kind, Path.Combine(dir, baseName + ext), kind == PdfPetaOutputKind.Png ? (double)_dpi.Value : null));
        }
        var existing = outputs.Where(x => File.Exists(x.Path)).Select(x => x.Path).ToArray();
        if (existing.Length > 0 && MessageBox.Show(this, "次のファイルを上書きします。\n" + string.Join("\n", existing), "上書き確認", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        try { var result = await RunAsync(BuildRequest(outputs), _shutdownCts.Token); if (_closing) return; ShowDiagnostics(result); MessageBox.Show(this, $"{outputs.Count}形式を保存しました。", "完了"); }
        catch (OperationCanceledException) when (_closing) { } catch (Exception ex) { if (!_closing) ShowError(ex); }
    }

    async Task<PdfPetaResult> RunAsync(PdfPetaRequest request, CancellationToken ct, PdfPetaRunOptions? options = null)
    {
        var errors = PdfPetaGuiPresenter.ValidateForGui(request); if (errors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, errors));
        await _runGate.WaitAsync(ct); SetBusy(true); try { var writer = new ProgressWriter(AppendLog); return await Task.Run(() => PdfPetaPipeline.Run(request, writer, options, ct), ct); } finally { SetBusy(false); _runGate.Release(); }
    }
    void SetBusy(bool busy) { if (InvokeRequired) { BeginInvoke(() => SetBusy(busy)); return; } _busy = busy; UpdateUiState(); _status.Text = busy ? "処理中…（キャンセルはステージ境界で反映）" : ""; }
    void UpdateUiState() { bool save = PreviewPolicy.CanSave(_busy, _previewMode, _closing); _paste.Enabled = _save.Enabled = _saveMulti.Enabled = save; _refresh.Enabled = !_busy && _previewMode == PreviewMode.Normal && !_closing; _selectCrop.Enabled = _pickColor.Enabled = !_closing && (_previewMode != PreviewMode.Normal || (!_busy && File.Exists(_input.Text))); }
    void ShowDiagnostics(PdfPetaResult result) { _diagnostics.Items.Clear(); foreach (var d in result.Diagnostics) _diagnostics.Items.Add(new ListViewItem(new[] { d.Type, d.Count.ToString(), string.Join(", ", d.Targets) })); }
    void ShowError(Exception ex) { _status.Text = ex.Message; MessageBox.Show(this, ex.Message, "CADPeta エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    void ShowPreviewError(Exception ex) { _status.Text = $"プレビュー未更新: {ex.Message}"; }
    void AppendLog(string line) { if (_closing || IsDisposed || Disposing || !IsHandleCreated) return; try { BeginInvoke(() => { if (!_closing && !IsDisposed) _log.AppendText(line + Environment.NewLine); }); } catch (InvalidOperationException) { } }

    async void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing)
        {
            e.Cancel = ShutdownClosePolicy.ShouldCancel(_closing, _shutdownReady);
            return;
        }
        _closing = true; ResetInteraction(); UpdateUiState(); _debounce.Stop(); _previewCts?.Cancel(); _shutdownCts.Cancel();
        if (_runGate.CurrentCount == 0)
        {
            e.Cancel = true; _status.Text = "処理を安全に終了しています…";
            await _runGate.WaitAsync(); _runGate.Release();
            _shutdownReady = true;
            Close();
        }
        else
        {
            _shutdownReady = true;
        }
    }

    void DisposeResources()
    {
        _previewCts?.Cancel(); _previewCts?.Dispose(); _previewCts = null;
        var image = _preview.Image; _preview.Image = null; image?.Dispose();
        _paletteTips.Dispose(); _appIcon?.Dispose(); _appIcon = null;
        _debounce.Dispose(); _runGate.Dispose(); _shutdownCts.Dispose(); _session.Dispose();
    }

    sealed class ProgressWriter(Action<string> report) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => report(value ?? "");
    }

    sealed record PreviewSnapshot(int Generation, PreviewMode Mode, Size ImageSize, double PageWidthMm,
        double PageHeightMm, IReadOnlyList<ColorUse> Inventory);
}

internal static class ShutdownClosePolicy
{
    public static bool ShouldCancel(bool closing, bool ready) => closing && !ready;
}
