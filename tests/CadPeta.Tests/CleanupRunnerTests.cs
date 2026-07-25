using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class CleanupRunnerTests
{
    [Fact]
    public void 複数の後始末を失敗後も全件試行する()
    {
        var calls = new List<int>();
        var ex = Assert.Throws<AggregateException>(() => CleanupRunner.RunAll(
            () => { calls.Add(1); throw new IOException("復元1"); },
            () => calls.Add(2),
            () => { calls.Add(3); throw new InvalidOperationException("復元3"); }));
        Assert.Equal(new[] { 1, 2, 3 }, calls);
        Assert.Contains("復元1", ex.ToString());
        Assert.Contains("復元3", ex.ToString());
    }

    [Fact]
    public void 本処理と後始末の両例外を保持する()
    {
        var scope = new FaultScope();
        var ex = Assert.Throws<AggregateException>(() => CleanupRunner.RunWithScope<int>(scope,
            () => throw new InvalidOperationException("発行失敗")));
        Assert.Contains("発行失敗", ex.ToString());
        Assert.Contains("復元失敗", ex.ToString());
        Assert.Equal(1, scope.Count);
    }

    [Fact]
    public void 例外伝播中の後始末失敗は元例外と後始末例外を保持する()
    {
        var original = new InvalidOperationException("確認失敗");
        var ex = Assert.Throws<AggregateException>(() =>
            CleanupRunner.RunAfterFailure(original, () => throw new IOException("破棄失敗")));
        Assert.Contains("確認失敗", ex.ToString());
        Assert.Contains("破棄失敗", ex.ToString());
    }

    sealed class FaultScope : IDisposable
    {
        public int Count;
        public void Dispose() { Count++; throw new IOException("復元失敗"); }
    }
}
