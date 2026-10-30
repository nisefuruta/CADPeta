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
