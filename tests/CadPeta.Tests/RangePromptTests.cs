// RangePrompt（範囲プロンプトの状態機械）を偽の IEditorPort で駆動して検査する。
// 実機（AutoCAD/互換CAD）を持たない環境でも、範囲（全体/窓）と選択絞り込みの独立性、
// オプションのトグル、PETA と構造SVG書き出し（PETADATA）の対話差を機械検証できる。
using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using CadPeta.Acquire.Common;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public class RangePromptTests
{
    static RangeChoice? Run(FakeEditor ed) =>
        RangePrompt.TryGetChoice(ed, TextWriter.Null, TextWriter.Null);

    [Fact]
    public void 先選択は絞り込みの初期値になり範囲プロンプトは省略されない()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone()); // 範囲 Enter=全体
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.True(c!.IsSelection);
        Assert.Null(c.Window); // 全体
        Assert.Single(ed.WindowKeywordSets); // 範囲プロンプトが出ている
    }

    [Fact]
    public void 先選択と二点窓は併用できる()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(0, 0, 100, 70)));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.True(c!.IsSelection);
        Assert.NotNull(c.Window);
    }

    [Fact]
    public void 先選択後のオブジェクト指定は後選択へ切り替えて対象を置換する()
    {
        var oldHandle = new object();
        var newHandle = new object();
        var ed = new FakeEditor { Pickfirst = oldHandle };
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordObject));
        ed.Selections.Enqueue(SelectionResult.OfSelected(newHandle));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(10, 20, 110, 90)));

        var c = Run(ed);

        Assert.NotNull(c);
        Assert.Same(newHandle, c!.SelectionHandle);
        Assert.NotSame(oldHandle, c.SelectionHandle);
        Assert.Equal(1, ed.SelectionPromptCount);
        Assert.Equal(new PlotWindow(10, 20, 110, 90), c.Window);
    }

    [Fact]
    public void 全体キーワードはPDF印刷経路全体になる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordAll));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.False(c!.IsSelection);
        Assert.Null(c.Window); // 全体
    }

    [Fact]
    public void Enter既定は全体になる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Null(c!.Window);
        Assert.False(c.IsSelection);
    }

    [Fact]
    public void オブジェクトキーワードは絞り込みを設定し範囲プロンプトへ戻る()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordObject));
        ed.Selections.Enqueue(SelectionResult.OfSelected(new object()));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone()); // 続けて範囲 Enter=全体
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.True(c!.IsSelection);
        Assert.Null(c.Window);
        Assert.Equal(2, ed.WindowKeywordSets.Count); // 選択後も範囲を必ず確定する
    }

    [Fact]
    public void オブジェクト選択と二点窓は併用できる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordObject));
        ed.Selections.Enqueue(SelectionResult.OfSelected(new object()));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(10, 20, 110, 90)));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.True(c!.IsSelection);
        Assert.NotNull(c.Window);
    }

    [Fact]
    public void オブジェクトキーワードの空選択は絞り込みを解除する()
    {
        var ed = new FakeEditor { Pickfirst = new object() }; // 先選択があっても解除できる
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordObject));
        ed.Selections.Enqueue(SelectionResult.OfEmpty());
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordAll));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.False(c!.IsSelection);
        Assert.Null(c.Window); // 全体
        Assert.Contains(ed.Lines, line => line.Contains("先選択を含む絞り込みを解除"));
    }

    [Fact]
    public void オブジェクト選択のキャンセルはコマンド中止になる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordObject));
        ed.Selections.Enqueue(SelectionResult.OfCancelled());
        Assert.Null(Run(ed));
    }

    [Fact]
    public void レイヤと文字輪郭は範囲プロンプトで切り替えられる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordLayer));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordTextOutline));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.True(c!.GroupByLayer);
        Assert.True(c.TextOutline);
    }

    [Fact]
    public void 二点窓はPDF印刷経路の窓になる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(10, 20, 110, 90)));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.False(c!.IsSelection);
        Assert.NotNull(c.Window);
        Assert.Equal(100, c.Window!.Value.Width, 9);
        Assert.Equal(70, c.Window.Value.Height, 9);
    }

    [Fact]
    public void 縮退窓は聞き直して次の有効窓を採る()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(5, 5, 5, 90))); // 幅0=縮退
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfValue(new PlotWindow(0, 0, 50, 40)));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(50, c!.Window!.Value.Width, 9);
        Assert.Contains(ed.Lines, l => l.Contains("指定し直す"));
    }

    [Fact]
    public void 余白キーワードは余白を確定し全体へ続く()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordMargin));
        ed.Doubles.Enqueue(PromptResult<double>.OfValue(7.5)); // 余白入力
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordAll));
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(7.5, c!.MarginMm, 9);
    }

    [Fact]
    public void PETADATA尺度プロンプトのLST切替は構造SVG書き出しオプションへ反映される()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Doubles.Enqueue(PromptResult<double>.OfKeyword(PluginDefaults.KeywordLayer));
        ed.Doubles.Enqueue(PromptResult<double>.OfKeyword(PluginDefaults.KeywordShxSubstitute));
        ed.Doubles.Enqueue(PromptResult<double>.OfKeyword(PluginDefaults.KeywordTextOutline));
        ed.Doubles.Enqueue(PromptResult<double>.OfValue(50)); // 尺度 1:50
        var c = RangePrompt.TryGetDataChoice(ed, TextWriter.Null, TextWriter.Null);
        Assert.NotNull(c);
        Assert.True(c!.GroupByLayer);
        Assert.True(c.ShxSubstitute);
        Assert.True(c.TextOutline);
        Assert.Equal(50, c.ScaleDenominator, 9);
    }

    [Fact]
    public void 全体指定のキャンセルはコマンド中止になる()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfCancelled());
        Assert.Null(Run(ed));
        Assert.Equal(ed.ScopeBeginCount, ed.ScopeDisposeCount);
    }

    [Theory]
    [InlineData(PromptOutcome.None)]
    [InlineData(PromptOutcome.Cancelled)]
    [InlineData(PromptOutcome.Value)]
    public void 窓プロンプトの全終了結果で空間scopeを復元する(PromptOutcome outcome)
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(outcome switch
        {
            PromptOutcome.None => PromptResult<PlotWindow>.OfNone(),
            PromptOutcome.Cancelled => PromptResult<PlotWindow>.OfCancelled(),
            _ => PromptResult<PlotWindow>.OfValue(new PlotWindow(0, 0, 10, 10)),
        });
        Run(ed);
        Assert.Equal(1, ed.ScopeBeginCount);
        Assert.Equal(1, ed.ScopeDisposeCount);
    }

    [Fact]
    public void 窓プロンプト例外でも空間scopeを復元する()
    {
        var expected = new InvalidOperationException("第2点取得失敗");
        var ed = new FakeEditor { PromptWindowException = expected };
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => Run(ed)));
        Assert.Equal(1, ed.ScopeDisposeCount);
    }

    [Fact]
    public void 窓プロンプトと空間復元の両例外を保持する()
    {
        var ed = new FakeEditor
        {
            PromptWindowException = new InvalidOperationException("点取得失敗"),
            ScopeDisposeException = new IOException("CVPORT復元失敗"),
        };
        var ex = Assert.Throws<AggregateException>(() => Run(ed));
        Assert.Contains("点取得失敗", ex.ToString());
        Assert.Contains("CVPORT復元失敗", ex.ToString());
    }

    [Fact]
    public void 構造SVG書き出しのGキーワードはグラデーションDPIを保持する()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Doubles.Enqueue(PromptResult<double>.OfKeyword(PluginDefaults.KeywordGradientDpi));
        ed.Doubles.Enqueue(PromptResult<double>.OfValue(300));
        ed.Doubles.Enqueue(PromptResult<double>.OfNone());
        var c = RangePrompt.TryGetDataChoice(ed, TextWriter.Null, TextWriter.Null);
        Assert.Equal(300, c!.GradientDpi, 9);
        Assert.Contains(ed.Lines, line => line.Contains("300dpi"));
    }

    [Fact]
    public void Bキーワードは絞り込みの有無に依らずEMF背景色を保持する()
    {
        var selection = new FakeEditor { Pickfirst = new object(), StringResponse = "#1234AB" };
        selection.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        selection.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var a = Run(selection)!;
        Assert.True(a.IsSelection);
        Assert.Equal(0x12 / 255.0, a.EmfBackground.R, 12);
        Assert.Equal(0xAB / 255.0, a.EmfBackground.B, 12);

        var plot = new FakeEditor { StringResponse = "#1234AB" };
        plot.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        plot.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordAll));
        var b = Run(plot)!;
        Assert.Equal(a.EmfBackground, b.EmfBackground);
    }

    [Fact]
    public void EMF背景色プロンプトは変更後の現在値を再表示する()
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue("#1234AB");
        ed.StringResponses.Enqueue(""); // 2回目は空入力（現在値を維持）
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(2, ed.StringMessages.Count);
        Assert.Contains("<#FFFFFF>", ed.StringMessages[0]); // 1回目は既定の白
        Assert.Contains("<#1234AB>", ed.StringMessages[1]); // 2回目は直前に変更した現在値
    }

    [Fact]
    public void EMF背景色の空入力は直前の値を維持する()
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue("#1234AB");
        ed.StringResponses.Enqueue("");
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(0x12 / 255.0, c!.EmfBackground.R, 12);
        Assert.Equal(0x34 / 255.0, c.EmfBackground.G, 12);
        Assert.Equal(0xAB / 255.0, c.EmfBackground.B, 12);
    }

    [Theory]
    [InlineData("# ABCDE")]
    [InlineData("#ABCDE ")]
    public void EMF背景色は空白混じりの16進を拒否する(string input)
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue(input);
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(PluginDefaults.DefaultEmfBackground, c!.EmfBackground); // 拒否されて既定のまま
        Assert.Contains(ed.Lines, l => l.Contains("#RRGGBB 形式"));
    }

    [Fact]
    public void EMF背景色は小文字混じりの6桁16進を受理する()
    {
        var ed = new FakeEditor();
        ed.StringResponses.Enqueue("#0aBc12");
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(PluginDefaults.KeywordEmfBackground));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var c = Run(ed);
        Assert.NotNull(c);
        Assert.Equal(0x0a / 255.0, c!.EmfBackground.R, 12);
        Assert.Equal(0xBc / 255.0, c.EmfBackground.G, 12);
        Assert.Equal(0x12 / 255.0, c.EmfBackground.B, 12);
    }

    [Fact]
    public void PETADATAは先選択を使い構造書き出しオプションだけを提示する()
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Doubles.Enqueue(PromptResult<double>.OfNone());
        var choice = RangePrompt.TryGetDataChoice(ed, TextWriter.Null, TextWriter.Null);
        Assert.NotNull(choice);
        var globals = ed.DoubleKeywordSets.Single().Select(k => k.Global).ToArray();
        Assert.Contains(PluginDefaults.KeywordShxSubstitute, globals);
        Assert.Contains(PluginDefaults.KeywordGradientDpi, globals);
        Assert.DoesNotContain(PluginDefaults.KeywordEmfBackground, globals);
    }

    [Fact]
    public void PETADATAの後選択キャンセルは安全に中止する()
    {
        var ed = new FakeEditor();
        ed.Selections.Enqueue(SelectionResult.OfCancelled());
        var log = new StringWriter();
        Assert.Null(RangePrompt.TryGetDataChoice(ed, log, TextWriter.Null));
        Assert.Contains("PETADATA を中止", log.ToString());
    }

    [Fact]
    public void 範囲プロンプトは絞り込みと出力オプションを提示し構造書き出し専用項目を含まない()
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        RangePrompt.TryGetChoice(ed, TextWriter.Null, TextWriter.Null);
        var globals = ed.WindowKeywordSets.Single().Select(k => k.Global).ToArray();
        Assert.Equal(new[]
        {
            PluginDefaults.KeywordAll, PluginDefaults.KeywordObject, PluginDefaults.KeywordMargin,
            PluginDefaults.KeywordLayer, PluginDefaults.KeywordTextOutline, PluginDefaults.KeywordEmfBackground,
        }, globals);
    }

    // ファイル出力(F)は PETAOPT の設定に置き換えたので、範囲のプロンプトでは受け付けない
    [Theory]
    [InlineData(PluginDefaults.KeywordShxSubstitute)]
    [InlineData(PluginDefaults.KeywordGradientDpi)]
    [InlineData("Fileoutput")]
    public void 範囲プロンプトは非提示の構造書き出しキーワードを受理しない(string keyword)
    {
        var ed = new FakeEditor();
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfKeyword(keyword));
        ed.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var log = new StringWriter();
        var choice = RangePrompt.TryGetChoice(ed, log, TextWriter.Null);
        Assert.NotNull(choice);
        Assert.False(choice!.ShxSubstitute);
        Assert.Equal(AcquireDefaults.GradientDefaultDpi, choice.GradientDpi);
        Assert.Contains("未知のキーワード", log.ToString());
    }

    [Theory]
    [InlineData(PluginDefaults.KeywordEmfBackground)]
    public void PETADATAは非提示の印刷用キーワードを受理しない(string keyword)
    {
        var ed = new FakeEditor { Pickfirst = new object() };
        ed.Doubles.Enqueue(PromptResult<double>.OfKeyword(keyword));
        ed.Doubles.Enqueue(PromptResult<double>.OfNone());
        var log = new StringWriter();
        var choice = RangePrompt.TryGetDataChoice(ed, log, TextWriter.Null);
        Assert.NotNull(choice);
        Assert.Equal(PluginDefaults.DefaultEmfBackground, choice!.EmfBackground);
        Assert.Contains("未知のキーワード", log.ToString());
    }

    [Fact]
    public void 先選択の採用は通常ログに記録しPETAとPETADATAの経路を区別する()
    {
        var print = new FakeEditor { Pickfirst = new object() };
        print.Windows.Enqueue(PromptResult<PlotWindow>.OfNone());
        var printLog = new StringWriter();
        var printVerbose = new StringWriter();
        RangePrompt.TryGetChoice(print, printLog, printVerbose);
        Assert.Contains("絞り込", printLog.ToString());
        Assert.DoesNotContain("DB 直接取得", printLog.ToString());
        Assert.Empty(printVerbose.ToString());

        var export = new FakeEditor { Pickfirst = new object() };
        export.Doubles.Enqueue(PromptResult<double>.OfNone());
        var exportLog = new StringWriter();
        var exportVerbose = new StringWriter();
        RangePrompt.TryGetDataChoice(export, exportLog, exportVerbose);
        Assert.Contains("構造SVG書き出し", exportLog.ToString());
        Assert.Empty(exportVerbose.ToString());
    }
}
