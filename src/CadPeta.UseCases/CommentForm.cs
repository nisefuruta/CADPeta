namespace CadPeta.UseCases;

/// <summary>
/// PETA のコメントのダイアログの入力（表示モデル）。入力したコメントを差し込んだファイル名（拡張子を除く）と、使えないコメントの理由を返す。
/// 検証はコマンドラインの入力と同じ <see cref="FileNameTemplate.ValidateComment"/>。引数の fields はコメントのほかの差し込みの値。
/// </summary>
public sealed class CommentForm
{
    readonly FileNameTemplate _template;
    readonly FileNameFields _fields;

    public CommentForm(FileNameTemplate template, FileNameFields fields)
    {
        _template = template;
        _fields = fields with { Comment = "" };
    }

    public string Comment { get; set; } = "";

    /// <summary>コメントが使えなければ理由（使えれば null）。</summary>
    public string? Error => _template.ValidateComment(Fields);

    /// <summary>コメントを差し込んだファイル名（連番は 1）。コメントが使えなければ空。</summary>
    public string Preview => Error == null ? _template.Expand(Fields, seq: 1) : "";

    FileNameFields Fields => _fields with { Comment = Comment };
}
