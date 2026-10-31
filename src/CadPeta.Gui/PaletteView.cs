using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CadPeta.Geometry;
using CadPeta.Transforms;

namespace CadPeta.Gui;

/// <summary>
/// 使用色の一覧（使用の多い順）の色見本。色が数千あっても重くならないように、ボタンを並べずに 1 つの部品で描き、
/// クリックした位置から色を求める。幅に合わせて折り返し、高さは行の数で決める（スクロールは外側で行う）。
/// </summary>
internal sealed class PaletteView : FrameworkElement
{
    // 色見本の大きさと間（px）
    const double Cell = 18;
    const double Gap = 3;

    readonly Pen _border;
    readonly ToolTip _tip = new() { Placement = System.Windows.Controls.Primitives.PlacementMode.Mouse };
    IReadOnlyList<ColorUse> _colors = Array.Empty<ColorUse>();
    Brush[] _brushes = Array.Empty<Brush>();

    public PaletteView(Brush border)
    {
        _border = new Pen(border, 1);
        _border.Freeze();
        // ツールチップは色見本ごとに中身を変えるので手で開閉し、マウスの位置に出す
        _tip.PlacementTarget = this;
        ToolTip = _tip;
        ToolTipService.SetInitialShowDelay(this, 0);
        Cursor = Cursors.Hand;
        AutomationProperties.SetAutomationId(this, "Palette");
        AutomationProperties.SetName(this, "使用色");
    }

    // 独自に描く部品は既定では UI オートメーションに出ないので、部品として見えるようにする（読み上げと画面の確認の試験）
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);

    /// <summary>色見本をクリックした。</summary>
    public event Action<ColorUse>? Picked;

    public IReadOnlyList<ColorUse> Colors
    {
        get => _colors;
        set
        {
            _colors = value;
            _brushes = new Brush[value.Count];
            for (int i = 0; i < value.Count; i++)
            {
                var (r, g, b) = value[i].Color.ToRgb8();
                _brushes[i] = GuiTheme.Brush((byte)r, (byte)g, (byte)b);
            }
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    int Columns(double width) => Math.Max(1, (int)((width + Gap) / (Cell + Gap)));

    protected override Size MeasureOverride(Size available)
    {
        // 幅の制限が無いとき（スクロールの中で横に広がるとき）は 10 列にする
        double width = Numbers.IsFinite(available.Width) ? available.Width : 10 * (Cell + Gap);
        int rows = (_colors.Count + Columns(width) - 1) / Columns(width);
        return new Size(width, Math.Max(0, rows * (Cell + Gap) - Gap));
    }

    protected override void OnRender(DrawingContext dc)
    {
        int columns = Columns(ActualWidth);
        for (int i = 0; i < _brushes.Length; i++)
            dc.DrawRectangle(_brushes[i], _border, new Rect(i % columns * (Cell + Gap) + 0.5, i / columns * (Cell + Gap) + 0.5, Cell - 1, Cell - 1));
    }

    int IndexAt(Point point)
    {
        int columns = Columns(ActualWidth);
        int column = (int)(point.X / (Cell + Gap)), row = (int)(point.Y / (Cell + Gap));
        int index = row * columns + column;
        bool inCell = point.X - column * (Cell + Gap) < Cell && point.Y - row * (Cell + Gap) < Cell;
        return column < columns && inCell && index < _colors.Count ? index : -1;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int index = IndexAt(e.GetPosition(this));
        _tip.Content = index < 0 ? null : $"{_colors[index].Color.ToHex()}（{_colors[index].Count}件）";
        _tip.IsOpen = index >= 0;
    }

    protected override void OnMouseLeave(MouseEventArgs e) => _tip.IsOpen = false;

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        int index = IndexAt(e.GetPosition(this));
        if (index >= 0) Picked?.Invoke(_colors[index]);
    }
}
