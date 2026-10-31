using System;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// ファイル名の形に {comment} があるとき、PETA は範囲の指定の後にコメントを聞く。画面のある CAD ではダイアログ、無ければコマンドラインで聞く。
// コマンドラインでは、Enter は空のコメント、差し込んだ名前がファイル名に使えなければ理由を出して聞き直し、Esc はコマンドを中止する
public sealed class CommentPromptTests
{
    static readonly FileNameTemplate Template = FileNameTemplate.Parse("{drawing}_{comment}").Template!;
    static readonly FileNameFields Fields = new("a", "Model", new DateTime(2026, 10, 28, 18, 0, 0), "");

    [Fact]
    public void 入力したコメントを返す()
    {
        var ed = new FakeEditor { StringResponse = "立面図" };

        Assert.Equal("立面図", CommentPrompt.TryGet(ed, TextWriter.Null, Template, Fields));
    }

    [Fact]
    public void Enterは空のコメント()
    {
        var ed = new FakeEditor { StringResponse = "" };

        Assert.Equal("", CommentPrompt.TryGet(ed, TextWriter.Null, Template, Fields));
    }

    [Fact]
    public void ファイル名に使えない文字は理由を出して聞き直す()
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue("a/b");
        ed.StringResponses.Enqueue("ab");

        Assert.Equal("ab", CommentPrompt.TryGet(ed, TextWriter.Null, Template, Fields));
        Assert.Equal(2, ed.StringMessages.Count);
        Assert.Contains(ed.Lines, l => l.Contains("a/b"));
    }

    [Fact]
    public void 差し込んだ名前がWindowsの予約の名前なら理由を出して聞き直す()
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue("CON");
        ed.StringResponses.Enqueue("立面");

        Assert.Equal("立面", CommentPrompt.TryGet(ed, TextWriter.Null, FileNameTemplate.Parse("{comment}.{drawing}").Template!, Fields));
        Assert.Equal(2, ed.StringMessages.Count);
        Assert.Contains(ed.Lines, l => l.Contains("CON"));
    }

    [Fact]
    public void Escはnullを返し中止したことを出す()
    {
        var ed = new FakeEditor { StringResponse = null };
        var log = new StringWriter();

        Assert.Null(CommentPrompt.TryGet(ed, log, Template, Fields));
        Assert.Contains(Msg.Text("comment.cancelled"), log.ToString());
    }

    // 画面のある CAD では、コマンドラインの代わりにダイアログで聞く（コマンドラインの問い合わせは見落とされやすい）

    [Fact]
    public void 画面があればダイアログで入力したコメントを返しコマンドラインでは聞かない()
    {
        var ed = new FakeEditor { HasScreen = true, DialogComment = "立面図" };

        Assert.Equal("立面図", CommentPrompt.TryGet(ed, TextWriter.Null, Template, Fields));
        Assert.Empty(ed.StringMessages);
    }

    [Fact]
    public void ダイアログのファイル名は形とほかの差し込みの値から作る()
    {
        var ed = new FakeEditor { HasScreen = true, DialogComment = "立面図" };

        CommentPrompt.TryGet(ed, TextWriter.Null, Template, Fields);

        Assert.Equal("a_立面図", Assert.Single(ed.CommentForms).Preview);
    }

    [Fact]
    public void ダイアログをキャンセルするとnullを返し中止したことを出す()
    {
        var ed = new FakeEditor { HasScreen = true, DialogComment = null };
        var log = new StringWriter();

        Assert.Null(CommentPrompt.TryGet(ed, log, Template, Fields));
        Assert.Contains(Msg.Text("comment.cancelled"), log.ToString());
    }
}
