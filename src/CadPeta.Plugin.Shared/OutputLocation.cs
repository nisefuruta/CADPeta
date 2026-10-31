// 出力先の決定（PETA は設定の出力先、PETADATA は図面と同じフォルダ。書き込めないときと、未保存の図面で起点が無いときは
// 利用者へ問い合わせ、無応答なら %TEMP%\CadPeta へ退避）。CAD 依存（保存状態・図面パス・文字列入力）は
// IEditorPort 越しに得て、フォルダ判定・生成は中立な IO で行う。
using System;
using System.IO;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

public static class OutputLocation
{
    /// <summary>Esc（対話キャンセル）時の挙動。呼び出し元コマンドの性格で使い分ける。</summary>
    enum CancelPolicy
    {
        /// <summary>退避先（%TEMP%\CadPeta）へ続行する（PETA/PETADIAG）。</summary>
        FallbackToTemp,
        /// <summary>成果物を作らず中止する（null を返す。PETADATA）。中止メッセージは呼び出し側が出す。</summary>
        Abort,
    }

    /// <summary>PETADATA用。出力先プロンプトをEscで中断した場合は成果物を作らずnull
    /// （中止メッセージは呼び出し側が出す。コマンド名をここへ焼き込まないため）。</summary>
    public static string? TryResolveExport(IEditorPort ed, TextWriter log) =>
        ResolveCore(ed, log, CancelPolicy.Abort, DrawingDir(ed));

    /// <summary>
    /// PETA 用。設定の出力先（相対パスは図面のフォルダが起点）に書き込めればそれを使う（フォルダは成果物を写すときに作る）。
    /// 起点が無いか書き込めなければ利用者へ問い合わせる。
    /// Escは退避先へ続行する。
    /// </summary>
    public static string ResolveFor(IEditorPort ed, TextWriter log, CadPetaSettings settings) =>
        ResolveCore(ed, log, CancelPolicy.FallbackToTemp, settings.ResolveOutputFolder(DrawingDir(ed)))!; // FallbackToTemp は null を返さない

    // 保存した図面のフォルダ（未保存なら null）
    static string? DrawingDir(IEditorPort ed) => ed.IsDrawingSaved ? Path.GetDirectoryName(ed.DrawingPath) : null;

    // 出力先決定の単一実装。Esc 時の挙動（退避先へ続行／中止）だけをポリシーで分岐し、
    // 図面フォルダ判定・利用者入力の評価・退避先の確保（CreateDirectory ガード＋文脈付き例外）は
    // 一本化する（旧 TryResolveExport は Resolve の複製で、退避先 CreateDirectory の
    // 例外変換だけが欠落していた）。
    // preferred は使いたい出力先（null は未保存の図面で起点が無い）。無ければ作り、書き込めればそれを使う
    static string? ResolveCore(IEditorPort ed, TextWriter log, CancelPolicy cancelPolicy, string? preferred)
    {
        if (preferred != null)
        {
            if (UnusableReason(preferred) is not { } reason)
                return preferred;
            log.WriteLine(Msg.Format("output.dir_unwritable", preferred, reason));
        }
        else
        {
            log.WriteLine(Msg.Text("output.unsaved"));
        }

        var fallback = Path.Combine(Path.GetTempPath(), PluginDefaults.FallbackSubdirName);
        bool abortOnCancel = cancelPolicy == CancelPolicy.Abort;
        var promptSuffix = abortOnCancel ? Msg.Text("output.esc_suffix") : "";
        var answer = ed.PromptString(Msg.Format("output.prompt", fallback, promptSuffix));
        if (answer == null)
        {
            if (abortOnCancel) return null; // 中止メッセージは呼び出し側が出す
            log.WriteLine(Msg.Text("output.cancelled_fallback"));
        }
        else if (!string.IsNullOrWhiteSpace(answer))
        {
            // 不正パス・権限不足など。理由を示した上で退避先へ進む（無言の付け替えはしない）
            var chosen = answer.Trim();
            if (UnusableReason(chosen) is not { } reason)
                return chosen;
            log.WriteLine(Msg.Format("output.chosen_create_failed", chosen, reason));
        }

        // 退避先も使えなければ出力手段がないため、ここで初めて中止（例外）とする
        if (UnusableReason(fallback) is { } fallbackReason)
            throw new IOException(Msg.Format("output.fallback_unusable", fallback, fallbackReason));
        return fallback;
    }

    /// <summary>
    /// フォルダを作り（あれば何もしない）、ファイルを 1 つ作って消して、書き込めるかを確かめる。使えなければ理由（例外の文を 1 行にしたもの）、
    /// 使えれば null。確かめるために作ったフォルダは消す（成果物を写すときに作り直す。何も出さない実行でフォルダを残さない）。
    /// </summary>
    static string? UnusableReason(string dir)
    {
        string? created = null;
        try
        {
            created = FirstMissing(Path.GetFullPath(dir));
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, Path.GetRandomFileName());
            using (File.Create(probe)) { }
            File.Delete(probe);
            return null;
        }
        catch (System.Exception ex) when (FileFailures.IsPathFailure(ex))
        {
            return FileFailures.OneLine(ex.Message);
        }
        finally
        {
            if (created != null)
                RemoveCreated(Path.GetFullPath(dir), created);
        }
    }

    // dir と、その上のフォルダのうち無いものの一番上（すべてあれば null）
    static string? FirstMissing(string dir)
    {
        string? missing = null;
        for (string? d = dir; d != null && !Directory.Exists(d); d = Path.GetDirectoryName(d))
            missing = d;
        return missing;
    }

    // 確かめるために作ったフォルダ（dir から top まで）を、下から 1 つずつ消す。空でないフォルダ（作った後にほかのプロセスが
    // ファイルを置いた）は Directory.Delete が失敗するので消さない
    static void RemoveCreated(string dir, string top)
    {
        try
        {
            // 作るのに途中で失敗したときは、下のフォルダが無いことがあるので、あるものだけ消す
            for (string? d = dir; d != null; d = Path.GetDirectoryName(d))
            {
                if (Directory.Exists(d))
                    Directory.Delete(d, recursive: false);
                if (string.Equals(d, top, StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        catch (System.Exception ex) when (FileFailures.IsPathFailure(ex))
        {
            // 消せなくても、残るのは書き込めると確かめたフォルダで、出力先の判定は変わらない（成果物はそこへ写す）
        }
    }}
