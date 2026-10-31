using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace CadPeta.Ui.Wpf;

/// <summary>
/// ダイアログのイベントの処理で、想定外の例外を外へ出さないための共通の入口（ダイアログごとに 1 つ持つ）。
/// WPF のイベントの処理から例外が出ると、ディスパッチャーを通って CAD のメッセージ ループまで届き、ハンドルされない例外の画面になる。
/// 想定外の例外が起きたら保持し、ダイアログを閉じる処理をディスパッチャーに 1 回だけ依頼する（処理の途中では閉じない）。
/// 保持した後は、このダイアログのほかの処理を実行しない。閉じる処理の例外も保持する。
/// ダイアログを開いた側は、ShowDialog / ShowModalWindow から戻った直後に <see cref="ThrowIfFailed"/> で投げ直し、コマンドの最外の捕捉へ渡す。
/// 入力の検証や保存の失敗のような想定した失敗は、例外にせずメッセージ欄に出す（ここには来ない）。
/// </summary>
internal sealed class DialogGuard
{
    readonly Window _window;
    readonly List<Exception> _failures = new();

    public DialogGuard(Window window) => _window = window;

    /// <summary>保持した例外を投げ直す。1 件ならスタックトレースを保ってそのまま、2 件以上なら全部を入れた AggregateException。</summary>
    public void ThrowIfFailed()
    {
        if (_failures.Count == 1) ExceptionDispatchInfo.Capture(_failures[0]).Throw();
        if (_failures.Count > 1) throw new AggregateException(_failures);
    }

    public void Run(Action action)
    {
        if (_failures.Count > 0) return;
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _failures.Add(ex);
            _window.Dispatcher.BeginInvoke(new Action(CloseAfterFailure));
        }
    }

    public RoutedEventHandler Routed(Action action) => (sender, args) => Run(action);

    // 閉じる処理そのものの失敗も保持する。ダイアログは開いたままになるが、ユーザーが閉じれば開いた側が投げ直す
    void CloseAfterFailure()
    {
        try { _window.Close(); }
        catch (Exception ex) { _failures.Add(ex); }
    }
}