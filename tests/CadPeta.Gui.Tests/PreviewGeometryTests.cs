using System;
using System.Linq;
using System.Drawing;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Gui.Tests;

public class PreviewGeometryTests
{
    [Fact]
    public void Letterbox_outside_is_rejected_and_drag_is_clamped()
    {
        Assert.False(PreviewGeometry.TryControlToImagePixel(new PointF(10, 50), new Size(200, 100), new Size(100, 100), out _));
        Assert.Equal(new PointF(0, 50), PreviewGeometry.ControlToClampedImagePixel(new PointF(10, 50), new Size(200, 100), new Size(100, 100)));
    }

    [Fact]
    public void Full_a4_image_maps_exactly_to_page_dimensions()
    {
        var mm = PreviewGeometry.ImageToPageMm(new PointF(1240, 1754), new Size(1240, 1754), 210, 297);
        Assert.Equal(210, mm.X); Assert.Equal(297, mm.Y);
    }

    [Fact]
    public void Pixel_index_never_reaches_width_or_height()
    {
        Assert.Equal(new Point(99, 79), PreviewGeometry.PixelIndex(new PointF(100, 80), new Size(100, 80)));
    }

    // 画面の長さ（スポイトが近くを探す範囲）を、縮小・拡大して表示した画像の画素の長さにする
    [Theory]
    [InlineData(200, 100, 4, 4)]
    [InlineData(50, 50, 4, 8)]
    [InlineData(400, 400, 4, 1)]
    public void 画面の長さを画像の画素の長さにする(int boxWidth, int boxHeight, double length, int expected)
    {
        Assert.Equal(expected, PreviewGeometry.ControlLengthToImagePixels(length, new Size(boxWidth, boxHeight), new Size(100, 100)));
    }

    [Fact]
    public void Ui_policy_keeps_save_disabled_in_modes_and_rejects_stale_generation()
    {
        Assert.True(PreviewPolicy.CanSave(false, PreviewMode.Normal, false));
        Assert.False(PreviewPolicy.CanSave(false, PreviewMode.SelectCrop, false));
        Assert.False(PreviewPolicy.CanSave(false, PreviewMode.PickColor, false));
        Assert.False(PreviewPolicy.CanSave(true, PreviewMode.Normal, false));
        Assert.True(PreviewPolicy.CanCommitPreview(4, 4, PreviewMode.PreparingPickColor, PreviewMode.PreparingPickColor));
        Assert.False(PreviewPolicy.CanCommitPreview(4, 5, PreviewMode.PreparingPickColor, PreviewMode.Normal));
        Assert.False(PreviewPolicy.CanCommitPreview(4, 4, PreviewMode.PreparingPickColor, PreviewMode.Normal));
        Assert.Equal((false, true), PreviewPolicy.RequestFlags(PreviewMode.SelectCrop));
        Assert.Equal((true, false), PreviewPolicy.RequestFlags(PreviewMode.PickColor));
        Assert.Equal((true, true), PreviewPolicy.RequestFlags(PreviewMode.Normal));
    }

    // 範囲の指定・スポイトの準備のプレビューが取り消されたとき、モードを通常に戻すのは、ほかに作り直しが無いときだけ。
    // 作り直しの予約（設定を変えた直後の待ち）や新しい作り直しがあれば、そちらが同じモードのプレビューを作る
    [Theory]
    [InlineData(false, 5, 5, false, true)]
    [InlineData(false, 5, 5, true, false)]
    [InlineData(false, 5, 6, false, false)]
    [InlineData(true, 5, 5, false, false)]
    public void 準備の取り消しでモードを戻すのはほかに作り直しが無いときだけ(bool closing, int generation, int currentGeneration, bool refreshPending, bool expected)
    {
        Assert.Equal(expected, PreviewPolicy.EndModeOnCancel(closing, PreviewMode.PreparingPickColor, PreviewMode.PreparingPickColor,
            generation, currentGeneration, refreshPending));
    }

    // 戻すのは、取り消されたプレビューが範囲の指定かスポイトの準備で、今もそのモードのときだけ
    [Theory]
    [InlineData("PreparingCrop", "PreparingCrop", true)]
    [InlineData("Normal", "Normal", false)]
    [InlineData("SelectCrop", "SelectCrop", false)]
    [InlineData("PreparingPickColor", "Normal", false)]
    [InlineData("PreparingCrop", "PreparingPickColor", false)]
    public void 準備の取り消しでモードを戻すのは準備中のモードのときだけ(string requested, string current, bool expected)
    {
        Assert.Equal(expected, PreviewPolicy.EndModeOnCancel(false, Mode(requested), Mode(current), 5, 5, false));
    }

    // 範囲の指定・スポイトのモードの中で設定を変えて作り直したプレビューも、そのモードのまま表示する（通常のモードへ黙って戻さない）
    [Theory]
    [InlineData("PreparingCrop", "SelectCrop")]
    [InlineData("SelectCrop", "SelectCrop")]
    [InlineData("PreparingPickColor", "PickColor")]
    [InlineData("PickColor", "PickColor")]
    [InlineData("Normal", "Normal")]
    public void 作り直したプレビューのモード(string requested, string expected)
    {
        Assert.Equal(Mode(expected), PreviewPolicy.ModeAfterPreview(Mode(requested)));
    }

    // スポイトの色番号の画像は、スポイトの準備でも、スポイトのモードの中で作り直したときも作る
    [Theory]
    [InlineData("PreparingPickColor", true)]
    [InlineData("PickColor", true)]
    [InlineData("SelectCrop", false)]
    [InlineData("Normal", false)]
    public void スポイトのモードでは色番号の画像を作る(string requested, bool expected)
    {
        Assert.Equal(expected, PreviewPolicy.NeedsPickMap(Mode(requested)));
    }

    // PreviewMode は製品の中だけの型なので、試験の引数はモードの名前で渡す
    static PreviewMode Mode(string name) => Enum.Parse<PreviewMode>(name);
    [Theory]
    [InlineData(true, true, "cropped")]
    [InlineData(true, false, "cropped")]
    [InlineData(false, true, "background-removed")]
    [InlineData(false, false, "acquired")]
    public void Inventory_boundary_matches_last_stage_before_color(bool crop, bool removeBackground, string expected) =>
        Assert.Equal(expected, PreviewPolicy.InventoryBoundary(crop, removeBackground));

    [Fact]
    public void Inventory_comparison_detects_color_and_count_changes()
    {
        var a = new[] { new ColorUse(IrRgb.ParseHex("#112233"), 2) };
        Assert.True(PreviewPolicy.SameInventory(a, a.ToArray()));
        Assert.False(PreviewPolicy.SameInventory(a, new[] { new ColorUse(IrRgb.ParseHex("#112233"), 3) }));
        Assert.False(PreviewPolicy.SameInventory(a, new[] { new ColorUse(IrRgb.ParseHex("#112234"), 2) }));
        Assert.False(PreviewPolicy.SameInventory(a, Array.Empty<ColorUse>()));
    }
}
