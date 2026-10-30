using System;
using System.Collections.Generic;
using System.Linq;
using CadPeta.Ir;

namespace CadPeta.Transforms;

public readonly record struct ColorUse(IrRgb Color, int Count);

public static class ColorInventory
{
    public static IReadOnlyList<ColorUse> Collect(IrDocument ir)
    {
        if (ir is null) throw new ArgumentNullException(nameof(ir));
        var counts = new Dictionary<(int R, int G, int B), int>();

        void Add(IrRgb? color, double opacity)
        {
            if (color is not { } c || opacity <= 0) return;
            var key = c.ToRgb8();
            counts[key] = counts.TryGetValue(key, out int count) ? count + 1 : 1;
        }

        void Visit(IEnumerable<IrNode> nodes)
        {
            foreach (var node in nodes)
                switch (node)
                {
                    case IrPath path:
                        Add(path.Stroke, path.StrokeOpacity);
                        Add(path.Fill, path.FillOpacity);
                        break;
                    case IrText text:
                        Add(text.Fill, text.FillOpacity);
                        break;
                    case IrGroup group:
                        Visit(group.Children);
                        break;
                }
        }

        Visit(ir.Nodes);
        return counts.OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key.R).ThenBy(x => x.Key.G).ThenBy(x => x.Key.B)
            .Select(x => new ColorUse(new IrRgb(x.Key.R / 255d, x.Key.G / 255d, x.Key.B / 255d), x.Value))
            .ToArray();
    }
}
