using System;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// 読み込み時の初期化（IExtensionApplication.Initialize）の処理を実行する。ここに置くのは、失敗してもコマンドを
/// 正しく実行できる処理（読み込みの記録、版の表示）だけで、失敗は報告の関数に渡して読み込みを続ける。
/// </summary>
public static class PluginStartup
{
    /// <summary>処理を実行する。失敗は <paramref name="report"/> に渡して続ける。</summary>
    public static void Run(string step, Action action, Action<string, Exception> report)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            try
            {
                report(step, ex);
            }
            catch (Exception reportEx) when (reportEx is not OutOfMemoryException and not StackOverflowException)
            {
                // 報告の失敗を読み込みの失敗にしない。失敗してもコマンドを正しく実行できる処理なので、報告できなくてもコマンドの結果は変わらない
            }
        }
    }
}
