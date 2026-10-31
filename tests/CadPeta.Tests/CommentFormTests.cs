using System;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA のコメントのダイアログの入力（表示モデル）。入力したコメントを差し込んだファイル名と、使えないコメントの理由を返す。
// 検証はコマンドラインの入力と同じ（FileNameTemplate.ValidateComment）
public sealed class CommentFormTests
{
    static readonly FileNameFields Fields = new("a", "Model", new DateTime(2026, 10, 28, 18, 0, 0), "");

    static CommentForm Form(string template) => new(FileNameTemplate.Parse(template).Template!, Fields);

    [Fact]
    public void コメントを差し込んだファイル名を表示する()
    {
        var form = Form("{drawing}_{tab}_{comment}");

        form.Comment = "立面";

        Assert.Null(form.Error);
        Assert.Equal("a_Model_立面", form.Preview);
    }

    [Fact]
    public void 空のコメントは隣の区切りを詰めたファイル名を表示する()
    {
        var form = Form("{drawing}_{tab}_{comment}");

        Assert.Null(form.Error);
        Assert.Equal("a_Model", form.Preview);
    }

    // ファイル名に使えない文字と、差し込んだ名前が Windows の予約の名前になるコメント
    [Theory]
    [InlineData("{drawing}_{tab}_{comment}", "立面/1")]
    [InlineData("{comment}.{drawing}", "CON")]
    public void 使えないコメントは理由を返しファイル名を表示しない(string template, string comment)
    {
        var form = Form(template);

        form.Comment = comment;

        Assert.Contains(comment, form.Error);
        Assert.Equal("", form.Preview);
    }
}
