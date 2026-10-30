using System.IO;

#if CADPETA_ACAD
using Autodesk.AutoCAD.EditorInput;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.EditorInput;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.EditorInput;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.EditorInput;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using System.Text;

// 既存アダプタ（PdfIrAcquirer・EmfWriter）は処理ログを TextWriter へ書く設計のため、
// CAD のコマンドラインへ転送する薄いアダプタを用意する。4 製品で同じ実装なので、
// CadSelectionPlotPort.cs と同じ方式（#if 分岐＋csproj の Compile Include リンク）で共有する。

internal sealed class EditorTextWriter(Editor editor) : TextWriter
{
    readonly StringBuilder _line = new();

    // コマンドラインへの転送自体が失敗した後は true。ホストの WriteMessage は文書
    // コンテキストが無効になると例外を投げる（COM 自動化で文書切替が重なった実機で
    // eInvalidDrawing を観測）。ここは最後の報告チャネルで、失敗をさらに報告する先が
    // 無い。例外を上へ通すとコマンド境界の catch 内の書き込みや finally の Flush から
    // 漏れてホストの未処理例外ダイアログになるため、この転送呼び出しに限り例外を止め、
    // 以後の転送を打ち切る（成果物のファイル出力には影響しない。痕跡はデバッグトレースへ残す）
    bool _editorLost;

    public override Encoding Encoding => Encoding.Unicode;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            Emit("\n" + _line);
            _line.Clear();
        }
        else if (value != '\r')
        {
            _line.Append(value);
        }
    }

    public override void Flush()
    {
        if (_line.Length > 0)
        {
            Emit("\n" + _line);
            _line.Clear();
        }
    }

    void Emit(string text)
    {
        CadPeta.Plugin.Shared.PromptTrace.WriteMessage(text.TrimStart('\n'));
        if (_editorLost)
            return;
        try
        {
            editor.WriteMessage(text);
        }
        catch (System.Exception ex)
        {
            _editorLost = true;
            System.Diagnostics.Trace.WriteLine(
                "[CadPeta] コマンドラインへの出力に失敗した。以後の表示を打ち切る: " + ex);
        }
    }
}
