using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class SelectionViewportPolicyTests
{
    [Theory]
    [InlineData(false, 2, true, SelectionViewportDisposition.Supported)]
    [InlineData(false, -1, true, SelectionViewportDisposition.Supported)]
    [InlineData(false, 0, true, SelectionViewportDisposition.Supported)]
    [InlineData(false, 1, true, SelectionViewportDisposition.Controller)]
    [InlineData(false, 0, false, SelectionViewportDisposition.Off)]
    [InlineData(false, -1, false, SelectionViewportDisposition.Off)]
    [InlineData(true, 2, true, SelectionViewportDisposition.ModelSpace)]
    public void 表示状態と役割を別々に分類する(bool modelSpace, int number, bool on,
        SelectionViewportDisposition expected)
    {
        Assert.Equal(expected, SelectionViewportPolicy.Classify(modelSpace, number, on, 1));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    public void 制御Viewport番号だけを識別する(int number, bool expected)
    {
        Assert.Equal(expected, SelectionViewportPolicy.IsController(number, 1));
    }
}
