using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;

namespace CadPeta.Tests;

// スクリプト化した偽エディタ。各プロンプトはキューへ積んだ応答を順に返す。
internal sealed class FakeEditor : IEditorPort
{
    public bool IsDrawingSaved { get; set; } = true;
    public string DrawingPath { get; set; } = @"C:\dwg\test.dwg";
    public string DrawingStem { get; set; } = "test";
    public string CurrentLayoutName { get; set; } = "Model";

    public object? Pickfirst;
    public Queue<SelectionResult> Selections { get; } = new();
    public Queue<PromptResult<PlotWindow>> Windows { get; } = new();
    public Queue<PromptResult<double>> Doubles { get; } = new();
    public Queue<PromptResult<string>> KeywordAnswers { get; } = new();
    public List<IReadOnlyList<PromptKeyword>> KeywordSets { get; } = new();
    public bool HasScreen { get; set; }
    // コメントのダイアログで入力して OK を押す文字列。null ならキャンセルする
    public string? DialogComment { get; set; }
    public List<CommentForm> CommentForms { get; } = new();
    public string? StringResponse { get; set; }
    // 入力のたびに呼んで応答を決める（入力の時点の状態を試験が確かめるため）。null なら StringResponses・StringResponse を使う
    public Func<string, string?>? StringResponder { get; set; }
    // 1回の PromptString ごとに異なる応答を返したいテスト向け。空なら StringResponse を使う。
    public Queue<string?> StringResponses { get; } = new();
    public List<string> StringMessages { get; } = new();
    public List<string> Lines { get; } = new();
    public List<IReadOnlyList<PromptKeyword>> DoubleKeywordSets { get; } = new();
    public List<IReadOnlyList<PromptKeyword>> WindowKeywordSets { get; } = new();
    public int ScopeBeginCount;
    public int ScopeDisposeCount;
    public int SelectionPromptCount;
    public Exception? PromptWindowException;
    public Exception? ScopeDisposeException;

    public object? GetPickfirstSelection() => Pickfirst;
    public SelectionResult PromptSelection()
    {
        SelectionPromptCount++;
        return Selections.Dequeue();
    }
    public string? PromptString(string message)
    {
        StringMessages.Add(message);
        if (StringResponder != null) return StringResponder(message);
        return StringResponses.Count > 0 ? StringResponses.Dequeue() : StringResponse;
    }
    public bool ShowCommentDialog(CommentForm form)
    {
        CommentForms.Add(form);
        if (DialogComment == null) return false;
        form.Comment = DialogComment;
        return true;
    }
    public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
        double defaultValue, bool useDefault, bool allowNone, bool allowZero, bool allowNegative)
    {
        DoubleKeywordSets.Add(keywords);
        return Doubles.Dequeue();
    }
    public PromptResult<string> PromptKeywords(string message, IReadOnlyList<PromptKeyword> keywords)
    {
        KeywordSets.Add(keywords);
        return KeywordAnswers.Dequeue();
    }
    public void WriteLine(string message) => Lines.Add(message);
    public IDisposable BeginWindowPickScope(TextWriter verbose)
    {
        ScopeBeginCount++;
        return new Noop(this);
    }
    public PromptResult<PlotWindow> PromptWindow(string firstMessage,
        IReadOnlyList<PromptKeyword> keywords, string secondMessage)
    {
        WindowKeywordSets.Add(keywords);
        if (PromptWindowException != null) throw PromptWindowException;
        return Windows.Dequeue();
    }

    sealed class Noop : IDisposable
    {
        readonly FakeEditor _owner;
        public Noop(FakeEditor owner) => _owner = owner;
        public void Dispose()
        {
            _owner.ScopeDisposeCount++;
            if (_owner.ScopeDisposeException != null) throw _owner.ScopeDisposeException;
        }
    }
}
