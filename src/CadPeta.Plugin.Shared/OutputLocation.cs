// 出力先の決定（既定は図面と同じフォルダ、書き込み不可・未保存時は利用者へ問い合わせ、
// 無応答なら %TEMP%\CadPeta へ退避）。CAD 依存（保存状態・図面パス・文字列入力）は
// IEditorPort 越しに得て、フォルダ判定・生成は中立な IO で行う。
using System.IO;

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
        ResolveCore(ed, log, CancelPolicy.Abort);

    /// <summary>出力フォルダを決定する。必要なら利用者へ問い合わせる。Escは退避先へ続行する。</summary>
    public static string Resolve(IEditorPort ed, TextWriter log) =>
        ResolveCore(ed, log, CancelPolicy.FallbackToTemp)!; // FallbackToTemp は null を返さない

    // 出力先決定の単一実装。Esc 時の挙動（退避先へ続行／中止）だけをポリシーで分岐し、
    // 図面フォルダ判定・利用者入力の評価・退避先の確保（CreateDirectory ガード＋文脈付き例外）は
    // 一本化する（旧 TryResolveExport は Resolve の複製で、退避先 CreateDirectory の
    // 例外変換だけが欠落していた）。
    static string? ResolveCore(IEditorPort ed, TextWriter log, CancelPolicy cancelPolicy)
    {
        if (ed.IsDrawingSaved)
        {
            var dwgDir = Path.GetDirectoryName(ed.DrawingPath);
            if (dwgDir != null && IsWritable(dwgDir))
                return dwgDir;
            log.WriteLine(Msg.Format("output.dwg_dir_unwritable", dwgDir ?? ""));
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
            var chosen = answer.Trim();
            try
            {
                Directory.CreateDirectory(chosen);
                if (IsWritable(chosen))
                    return chosen;
                log.WriteLine(Msg.Format("output.chosen_unwritable", chosen));
            }
            catch (System.Exception ex) when (IsPathFailure(ex))
            {
                // 不正パス・権限不足等。理由を示した上で退避先へ進む（無言の付け替えはしない）
                log.WriteLine(Msg.Format("output.chosen_create_failed", chosen, ex.Message));
            }
        }

        // 退避先も確保できなければ出力手段がないため、ここで初めて中止（例外）とする
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch (System.Exception ex) when (IsPathFailure(ex))
        {
            throw new IOException(Msg.Format("output.fallback_create_failed", fallback), ex);
        }
        if (!IsWritable(fallback))
            throw new IOException(Msg.Format("output.fallback_unwritable", fallback));
        return fallback;
    }

    public static string Stem(IEditorPort ed) => ed.DrawingStem;

    /// <summary>パス操作・ファイル生成で「その場所は使えない」を意味する例外の判別。
    /// これ以外（OutOfMemory 等の実行系異常）は握りつぶさず上へ通す。</summary>
    static bool IsPathFailure(System.Exception ex) =>
        ex is IOException                    // PathTooLongException・DirectoryNotFoundException を含む
           or UnauthorizedAccessException    // ACL 拒否
           or System.Security.SecurityException
           or NotSupportedException          // "con:" 等の非対応形式
           or ArgumentException;             // 不正文字・空パス

    static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, Path.GetRandomFileName());
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch (System.Exception ex) when (IsPathFailure(ex))
        {
            return false;
        }
    }
}
