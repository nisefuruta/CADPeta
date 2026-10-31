using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public class SelectionPlotOrchestratorTests
{
    sealed class Editor : IEditorPort
    {
        public bool Partial { get; set; }
        public Exception? PromptError { get; set; }
        public int PartialConfirmationCount { get; private set; }
        public bool IsDrawingSaved => true; public string DrawingPath => "a.dwg";
        public string DrawingStem => "a"; public string CurrentLayoutName => "Model";
        public object? GetPickfirstSelection() => null;
        public SelectionResult PromptSelection() => SelectionResult.OfEmpty();
        public bool HasScreen => false;
        public bool ShowCommentDialog(CommentForm form) => throw new InvalidOperationException("コメントのダイアログは開かない");
        public string? PromptString(string message)
        {
            if (PromptError != null) throw PromptError;
            // SelectionConfirmPrompts はこの PromptString だけで確認を組み立てる（Confirm* は
            // IEditorPort から削除済み）。文言で用途を判別してフェイク側の既定応答を返す
            if (message.Contains("部分出力する")) { PartialConfirmationCount++; return Partial ? "Yes" : "No"; }
            return null;
        }
        public PromptResult<double> PromptDouble(string message, IReadOnlyList<PromptKeyword> keywords,
            double defaultValue, bool useDefault, bool allowNone, bool allowZero, bool allowNegative) => PromptResult<double>.OfNone();
        public PromptResult<string> PromptKeywords(string message, IReadOnlyList<PromptKeyword> keywords) =>
            throw new InvalidOperationException("キーワードの入力は呼ばれない");
        public void WriteLine(string message) { }
        public IDisposable BeginWindowPickScope(TextWriter verbose) => new Nop();
        public PromptResult<PlotWindow> PromptWindow(string firstMessage, IReadOnlyList<PromptKeyword> keywords, string secondMessage) => PromptResult<PlotWindow>.OfNone();
        sealed class Nop : IDisposable { public void Dispose() { } }
    }
    sealed class Port : ISelectionPlotPort
    {
        public Session Value = new();
        public ISelectionPlotSession Begin(object selectionHandle, TextWriter log, TextWriter verbose) => Value;
    }
    sealed class Session : ISelectionPlotSession
    {
        public int SelectedCount { get; set; } = 1;
        public int SupportedCount { get; set; } = 1;
        public IReadOnlyList<UnsupportedSelectionItem> Unsupported { get; set; } = Array.Empty<UnsupportedSelectionItem>();
        public IPlotPort Plotter { get; } = null!;
        public bool Disposed { get; private set; }
        public Exception? DisposeError { get; set; }
        public void Dispose()
        {
            Disposed = true;
            if (DisposeError != null) throw DisposeError;
        }
    }

    static ISelectionPlotSession? Begin(Port p, Editor editor, StringWriter? log = null) =>
        SelectionPlotOrchestrator.Begin(p, editor, new object(),
            (TextWriter?)log ?? TextWriter.Null, TextWriter.Null);

    [Fact] public void 非対応混在は既定で中止しセッションを後始末する()
    {
        var p = new Port(); p.Value.Unsupported = new[] { new UnsupportedSelectionItem("Xref", "1A", "unsupported") };
        var log = new StringWriter();
        Assert.Null(Begin(p, new Editor(), log));
        Assert.True(p.Value.Disposed);
        Assert.Contains("Xref 1A", log.ToString());
    }

    [Fact] public void 除外の承認時はセッションを返し後始末しない()
    {
        var p = new Port(); p.Value.Unsupported = new[] { new UnsupportedSelectionItem("Xref", "1A", "unsupported") };
        var editor = new Editor { Partial = true };
        var session = Begin(p, editor);
        Assert.Same(p.Value, session);
        Assert.False(p.Value.Disposed);
        Assert.Equal(1, editor.PartialConfirmationCount);
    }

    [Fact] public void 除外なしは確認を出さずセッションを返す()
    {
        var p = new Port();
        var editor = new Editor();
        Assert.Same(p.Value, Begin(p, editor));
        Assert.Equal(0, editor.PartialConfirmationCount);
    }

    [Fact] public void 全件非対応なら部分出力確認を出さない()
    {
        var p = new Port();
        p.Value.SupportedCount = 0;
        p.Value.Unsupported = new[] { new UnsupportedSelectionItem("Xref", "1A", "外部参照") };
        var editor = new Editor { Partial = true };
        var log = new StringWriter();
        Assert.Null(Begin(p, editor, log));
        Assert.True(p.Value.Disposed);
        Assert.Contains("1A", log.ToString());
        Assert.Contains("Xref", log.ToString());
        Assert.Equal(0, editor.PartialConfirmationCount);
    }

    [Fact] public void 除外確認とセッション破棄の両失敗を保持する()
    {
        var p = new Port();
        p.Value.Unsupported = new[] { new UnsupportedSelectionItem("Xref", "1A", "外部参照") };
        p.Value.DisposeError = new IOException("セッション破棄失敗");
        var editor = new Editor { PromptError = new InvalidOperationException("確認入力失敗") };

        var ex = Assert.Throws<AggregateException>(() => Begin(p, editor));

        Assert.True(p.Value.Disposed);
        Assert.Contains("確認入力失敗", ex.ToString());
        Assert.Contains("セッション破棄失敗", ex.ToString());
    }
}
