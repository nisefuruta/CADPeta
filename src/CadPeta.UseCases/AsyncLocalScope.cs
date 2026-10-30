using System;
using System.Threading;

namespace CadPeta.UseCases;

/// <summary>
/// 現在の実行コンテキストの値(AsyncLocal)を一時的に差し替え、Dispose で直前の値へ戻す。
/// using で入れ子の順に解除する。多重の Dispose では二重に戻さない。
/// </summary>
internal sealed class AsyncLocalScope<T> : IDisposable
{
    readonly AsyncLocal<T> _slot;
    readonly T _previous;
    bool _disposed;

    AsyncLocalScope(AsyncLocal<T> slot, T previous)
    {
        _slot = slot;
        _previous = previous;
    }

    /// <summary><paramref name="slot"/> を <paramref name="value"/> にし、戻すための範囲を返す。</summary>
    public static IDisposable Begin(AsyncLocal<T> slot, T value)
    {
        var scope = new AsyncLocalScope<T>(slot, slot.Value);
        slot.Value = value;
        return scope;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _slot.Value = _previous;
    }
}
