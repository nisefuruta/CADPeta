using System.Linq;
using CadPeta.Ir;
using CadPeta.Transforms;
using Xunit;

namespace CadPeta.Tests;

public class ColorInventoryTests
{
    [Fact]
    public void Collects_visible_attributes_recursively_and_orders_deterministically()
    {
        var red = IrRgb.ParseHex("#FF0000"); var blue = IrRgb.ParseHex("#0000FF");
        var hidden = new IrPath { Stroke = IrRgb.ParseHex("#00FF00"), StrokeOpacity = 0 };
        var group = new IrGroup(); group.Children.Add(new IrPath { Stroke = red, Fill = blue });
        var ir = new IrDocument(); ir.Nodes.Add(group); ir.Nodes.Add(new IrText { Fill = red }); ir.Nodes.Add(hidden); ir.Nodes.Add(new IrImage());
        var result = ColorInventory.Collect(ir);
        Assert.Equal(new[] { "#FF0000", "#0000FF" }, result.Select(x => x.Color.ToHex()));
        Assert.Equal(new[] { 2, 1 }, result.Select(x => x.Count));
    }

    [Fact]
    public void Equal_counts_sort_by_rgb_and_quantized_values_merge()
    {
        var ir = new IrDocument();
        ir.Nodes.Add(new IrPath { Stroke = new IrRgb(1 / 255d, 0, 0), Fill = new IrRgb(1.1 / 255d, 0, 0) });
        ir.Nodes.Add(new IrText { Fill = IrRgb.ParseHex("#000001") });
        var result = ColorInventory.Collect(ir);
        Assert.Equal(new[] { "#010000", "#000001" }, result.Select(x => x.Color.ToHex()));
        Assert.Equal(new[] { 2, 1 }, result.Select(x => x.Count));
    }

    [Fact]
    public void Rgb8_uses_existing_to_even_rounding_and_hex()
    {
        Assert.Equal((0, 2, 255), new IrRgb(.5 / 255, 1.5 / 255, 1).ToRgb8());
        Assert.Equal("#0002FF", new IrRgb(.5 / 255, 1.5 / 255, 1).ToHex());
    }
}
