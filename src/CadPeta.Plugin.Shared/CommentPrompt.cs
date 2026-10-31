using System.IO;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// ファイル名のコメント（{comment}）の入力。範囲などの指定の後に聞く。画面のある CAD ではダイアログで聞く（コマンドラインの問い合わせは
/// 見落とされやすい）。ダイアログは使えないコメントでは OK を押せず、キャンセルはコマンドの中止。
/// 画面の無い CAD ではコマンドラインで聞く。Enter は空のコメント、差し込んだ名前がファイル名に使えなければ（使えない文字、
/// Windows の予約の名前、末尾の . と空白）理由を出して聞き直し、Esc（null）はコマンドの中止。
/// 引数の fields はコメントのほかの差し込みの値。
/// </summary>
public static class CommentPrompt
{
    public static string? TryGet(IEditorPort ed, TextWriter log, FileNameTemplate template, FileNameFields fields)
    {
        string? comment = ed.HasScreen ? FromDialog(ed, template, fields) : FromCommandLine(ed, template, fields);
        if (comment == null)
            log.WriteLine(Msg.Text("comment.cancelled"));
        return comment;
    }

    static string? FromDialog(IEditorPort ed, FileNameTemplate template, FileNameFields fields)
    {
        var form = new CommentForm(template, fields);
        return ed.ShowCommentDialog(form) ? form.Comment : null;
    }

    static string? FromCommandLine(IEditorPort ed, FileNameTemplate template, FileNameFields fields)
    {
        while (true)
        {
            string? answer = ed.PromptString(Msg.Text("comment.prompt"));
            if (answer == null)
                return null;
            if (template.ValidateComment(fields with { Comment = answer }) is not { } reason)
                return answer;
            ed.WriteLine(reason);
        }
    }
}
