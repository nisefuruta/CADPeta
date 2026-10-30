using System;
using System.Runtime.ExceptionServices;

namespace CadPeta.Plugin.Shared;

/// <summary>MSPACEから窓pick用PSPACEへ一時切替し、開始途中の例外も含めて元状態へ戻す。</summary>
public static class WindowPickSpaceScope
{
    public static bool NeedsPaperSpace(short tileMode, short activeViewport, short paperViewport) =>
        tileMode == 0 && activeViewport != paperViewport;

    public static IDisposable Enter(short tileMode, short activeViewport, short paperViewport,
                                    Action switchToPaper, Action restoreModelViewport,
                                    Action reportSwitch)
    {
        if (!NeedsPaperSpace(tileMode, activeViewport, paperViewport)) return Noop.Instance;
        var scope = new RestoreScope(restoreModelViewport);
        try
        {
            // SDKが状態変更後に例外を返す場合にも復元を試すため、呼出前に復元対象とする。
            scope.RestoreRequired = true;
            switchToPaper();
            reportSwitch();
            return scope;
        }
        catch (Exception original)
        {
            try { scope.Dispose(); }
            catch (Exception restore) { throw new AggregateException("窓pick空間の開始と復元に失敗した", original, restore); }
            ExceptionDispatchInfo.Capture(original).Throw();
            throw;
        }
    }

    sealed class RestoreScope : IDisposable
    {
        readonly Action _restore;
        bool _disposed;
        internal bool RestoreRequired;
        internal RestoreScope(Action restore) => _restore = restore;
        public void Dispose()
        {
            if (_disposed) return;
            if (RestoreRequired) _restore();
            _disposed = true;
        }
    }

    sealed class Noop : IDisposable
    {
        internal static readonly Noop Instance = new();
        public void Dispose() { }
    }
}
