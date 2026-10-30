using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

public sealed class PromptBoundaryTests
{
    static readonly PromptKeyword[] PetaKeywords =
    {
        new(PluginDefaults.KeywordAll, "A", "全体(A)"),
        new(PluginDefaults.KeywordObject, "O", "オブジェクト(O)"),
        new(PluginDefaults.KeywordMargin, "M", "余白(M)"),
        new(PluginDefaults.KeywordLayer, "L", "レイヤ(L)"),
        new(PluginDefaults.KeywordTextOutline, "T", "文字輪郭(T)"),
        new(PluginDefaults.KeywordFileOutput, "F", "ファイル(F)"),
        new(PluginDefaults.KeywordEmfBackground, "B", "背景(B)"),
    };

    static readonly PromptKeyword[] PetaDataKeywords =
    {
        new(PluginDefaults.KeywordMargin, "M", "余白(M)"),
        new(PluginDefaults.KeywordLayer, "L", "レイヤ(L)"),
        new(PluginDefaults.KeywordTextOutline, "T", "文字輪郭(T)"),
        new(PluginDefaults.KeywordShxSubstitute, "S", "SHX代替(S)"),
        new(PluginDefaults.KeywordGradientDpi, "G", "グラデーション(G)"),
    };

    [Fact]
    public void Resolver_PETAの全短縮名をグローバル名へ解決する()
    {
        foreach (var keyword in PetaKeywords)
        {
            Assert.True(PromptKeywordResolver.TryResolveGlobal(
                keyword.Local, PetaKeywords, out var global));
            Assert.Equal(keyword.Global, global);
        }
    }

    [Fact]
    public void Resolver_PETADATAの全短縮名をグローバル名へ解決する()
    {
        foreach (var keyword in PetaDataKeywords)
        {
            Assert.True(PromptKeywordResolver.TryResolveGlobal(
                keyword.Local, PetaDataKeywords, out var global));
            Assert.Equal(keyword.Global, global);
        }
    }

    [Fact]
    public void Resolver_グローバル名と表示名と下線付きグローバル名を解決する()
    {
        var keyword = PetaKeywords.Single(k => k.Local == "O");

        Assert.Equal(keyword.Global,
            PromptKeywordResolver.ResolveGlobal(keyword.Global, PetaKeywords));
        Assert.Equal(keyword.Global,
            PromptKeywordResolver.ResolveGlobal(keyword.Display, PetaKeywords));
        Assert.Equal(keyword.Global,
            PromptKeywordResolver.ResolveGlobal("_" + keyword.Global, PetaKeywords));
        Assert.Equal(keyword.Global,
            PromptKeywordResolver.ResolveGlobal("o", PetaKeywords));
    }

    [Fact]
    public void Resolver_未知値とnullを解決しない()
    {
        Assert.False(PromptKeywordResolver.TryResolveGlobal(
            "Unknown", PetaKeywords, out var unknown));
        Assert.Equal(string.Empty, unknown);
        Assert.False(PromptKeywordResolver.TryResolveGlobal(
            null, PetaKeywords, out _));
        Assert.Throws<InvalidOperationException>(() =>
            PromptKeywordResolver.ResolveGlobal("Unknown", PetaKeywords));
    }

    [Fact]
    public void PromptTrace_未設定時はファイル書込処理を呼ばない()
    {
        bool called = false;

        PromptTrace.WriteCore(null, "PromptWindow.GetPoint", "Keyword", "O",
            valueIsNull: false, count: null,
            (_, _) => called = true);

        Assert.False(called);
    }

    [Fact]
    public void PromptTrace_JSONとして特殊文字をescapeしnullを保持する()
    {
        string? line = null;
        const string raw = "O\"\\\r\n日本語";

        PromptTrace.WriteCore("trace.jsonl", "PromptWindow.GetPoint", "Keyword", raw,
            valueIsNull: true, count: null,
            (_, json) => line = json);

        Assert.NotNull(line);
        using var document = JsonDocument.Parse(line!);
        var root = document.RootElement;
        Assert.Equal("PromptWindow.GetPoint", root.GetProperty("operation").GetString());
        Assert.Equal(raw, root.GetProperty("stringResult").GetString());
        Assert.True(root.GetProperty("valueIsNull").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("count").ValueKind);

        PromptTrace.WriteCore("trace.jsonl", "PromptSelection.GetSelection", "None", null,
            valueIsNull: true, count: null,
            (_, json) => line = json);
        using var nullDocument = JsonDocument.Parse(line!);
        Assert.Equal(JsonValueKind.Null,
            nullDocument.RootElement.GetProperty("stringResult").ValueKind);
    }

    [Fact]
    public void PromptTrace_書込失敗を呼出元へ返す()
    {
        var error = Assert.Throws<IOException>(() =>
            PromptTrace.WriteCore("trace.jsonl", "PromptSelection.GetSelection", "OK", null,
                valueIsNull: false, count: 2,
                (_, _) => throw new IOException("試験用の書込失敗")));

        Assert.Equal("試験用の書込失敗", error.Message);
    }

    [Fact]
    public void PromptTrace_製品用書込は計測先失敗を本処理へ返さない()
    {
        var error = Record.Exception(() =>
            PromptTrace.TryWriteCore("trace.jsonl", "Command.End", "End", null,
                valueIsNull: true, count: null,
                (_, _) => throw new IOException("試験用の書込失敗")));

        Assert.Null(error);
    }
}
