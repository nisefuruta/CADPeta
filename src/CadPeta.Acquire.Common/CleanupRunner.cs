using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;

namespace CadPeta.Acquire.Common;

/// <summary>後始末を全件試行し、本処理例外と復元例外を失わずに報告する。</summary>
public static class CleanupRunner
{
    public static void RunAll(params Action[] cleanups)
    {
        var failures = new List<Exception>();
        foreach (var cleanup in cleanups)
        {
            try { cleanup(); }
            catch (Exception ex) { failures.Add(ex); }
        }
        if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1) throw new AggregateException("複数の後始末に失敗した", failures);
    }

    public static T RunWithScope<T>(IDisposable scope, Func<T> operation)
    {
        T result;
        try
        {
            result = operation();
        }
        catch (Exception original)
        {
            try { scope.Dispose(); }
            catch (Exception cleanup)
            {
                if (ReferenceEquals(original, cleanup)) throw;
                throw new AggregateException("本処理と後始末の両方に失敗した", original, cleanup);
            }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
        scope.Dispose();
        return result;
    }

    public static T RunWithCleanup<T>(Func<T> operation, Action cleanup) =>
        RunWithScope(new ActionScope(cleanup), operation);

    /// <summary>本処理例外の伝播中に後始末し、後始末も失敗した場合は両方を保持する。</summary>
    public static void RunAfterFailure(Exception original, Action cleanup)
    {
        try { cleanup(); }
        catch (Exception cleanupError)
        {
            if (ReferenceEquals(original, cleanupError)) throw;
            throw new AggregateException("本処理と後始末の両方に失敗した", original, cleanupError);
        }
    }

    sealed class ActionScope : IDisposable
    {
        readonly Action _cleanup;
        bool _disposed;
        internal ActionScope(Action cleanup) => _cleanup = cleanup;
        public void Dispose()
        {
            if (_disposed) return;
            _cleanup();
            _disposed = true;
        }
    }
}
