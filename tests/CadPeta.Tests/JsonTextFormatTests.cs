using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CadPeta.Ir;
using CadPeta.Plugin.Shared;
using Xunit;

namespace CadPeta.Tests;

// プラグインが書く JSON（診断の diag.json、試験の記録の prompt-trace、側 DB の記録）の書式の約束。
// 試験のスクリプトと解析の道具がこの書式を読む。プロパティ名、日本語と特殊文字、null、空配列、数値と真偽値を、
// JSON として読み直して確かめる。直列化の部品の型情報（"__type"）は書かない。
// プラグインは System.Text.Json を参照しない（net48 で、その依存の System.Memory などの版が PdfPig の依存と食い違い、
// プラグインは bindingRedirect を持てないので読み込みに失敗した。2026-10-28、AutoCAD 2024）
public class JsonTextFormatTests
{
    const string Special = "日本語 \"引用\" \\ / \t\r\n<&>";

    // JSON を書く JsonText はユースケース層にあり、プラグインと同じプロセスに読み込まれる
    [Theory]
    [InlineData(typeof(PromptTrace))]
    [InlineData(typeof(CadPeta.UseCases.JsonText))]
    public void プラグインが読み込むアセンブリはSystem_Text_Jsonを参照しない(Type type)
    {
        var names = type.Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();
        Assert.DoesNotContain("System.Text.Json", names);
    }

    [Fact]
    public void 診断のdiag_jsonは種別ごとの件数と対象の配列()
    {
        using var temp = new TempFolder();
        string svg = Path.Combine(temp.Path, "out.svg");
        var diag = new IrDiagnostics();
        diag.Add("背景除去", Special);
        diag.Add("背景除去", "2");
        diag.Add("輪郭化", "x");

        DiagnosticsReport.WriteDiagJson(TextWriter.Null, svg, diag);

        string json = File.ReadAllText(svg + ".diag.json");
        Assert.DoesNotContain("__type", json);
        using var document = JsonDocument.Parse(json);
        var entries = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(new[] { "type", "count", "targets" }, entries[0].EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("背景除去", entries[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Number, entries[0].GetProperty("count").ValueKind);
        Assert.Equal(2, entries[0].GetProperty("count").GetInt32());
        Assert.Equal(new[] { Special, "2" }, entries[0].GetProperty("targets").EnumerateArray().Select(t => t.GetString()).ToArray());
    }

    [Fact]
    public void 診断の項目が無ければdiag_jsonは空配列()
    {
        using var temp = new TempFolder();
        string svg = Path.Combine(temp.Path, "out.svg");

        DiagnosticsReport.WriteDiagJson(TextWriter.Null, svg, new IrDiagnostics());

        using var document = JsonDocument.Parse(File.ReadAllText(svg + ".diag.json"));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
        Assert.Empty(document.RootElement.EnumerateArray());
    }

    [Fact]
    public void 試験の記録は1行の_JSONで値の型を保つ()
    {
        string? line = null;

        PromptTrace.WriteCore("trace.jsonl", "PromptWindow.GetPoint", "Keyword", Special,
            valueIsNull: false, count: 3, (_, json) => line = json);

        Assert.NotNull(line);
        Assert.DoesNotContain("\n", line!);
        Assert.DoesNotContain("__type", line!);
        using var document = JsonDocument.Parse(line!);
        var root = document.RootElement;
        Assert.Equal(new[] { "runId", "caseId", "pid", "command", "operation", "status", "stringResult", "valueIsNull", "count" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("pid").ValueKind);
        Assert.Equal(Special, root.GetProperty("stringResult").GetString());
        Assert.Equal(JsonValueKind.False, root.GetProperty("valueIsNull").ValueKind);
        Assert.Equal(3, root.GetProperty("count").GetInt32());
    }

    [Fact]
    public void 側DBの記録は辞書をオブジェクトで書きnullと空配列を保つ()
    {
        using var temp = new TempFolder();
        string stem = Path.Combine(temp.Path, "side");

        SideDatabaseDump.Write(stem, new SideDatabaseMetadata
        {
            Observation = "raw",
            CreationMethod = "Wblock",
            EntityCount = 0,
            EntityTypes = new Dictionary<string, int> { ["Line"] = 2, ["円"] = 1 },
            Extents = new SideDatabaseExtents { Min = Array.Empty<double>(), Max = new[] { 0.5, -1.25, 3.0 } },
            ExtentsUnavailableCount = 1,
            View = new Dictionary<string, string?> { ["Target"] = null },
            Layout = new Dictionary<string, string?> { ["LayoutName"] = Special },
        });

        string json = File.ReadAllText(stem + ".raw.json");
        Assert.DoesNotContain("__type", json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal(new[] { "observation", "creationMethod", "entityCount", "entityTypes", "extents", "extentsUnavailableCount", "view", "layout", "plotSettings" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("entityTypes").ValueKind);
        Assert.Equal(1, root.GetProperty("entityTypes").GetProperty("円").GetInt32());
        Assert.Empty(root.GetProperty("extents").GetProperty("min").EnumerateArray());
        Assert.Equal(new[] { 0.5, -1.25, 3.0 }, root.GetProperty("extents").GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("view").GetProperty("Target").ValueKind);
        Assert.Equal(Special, root.GetProperty("layout").GetProperty("LayoutName").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("plotSettings").ValueKind);
        Assert.Empty(root.GetProperty("plotSettings").EnumerateObject());
    }

    [Fact]
    public void 側DBの範囲が無ければnull()
    {
        using var temp = new TempFolder();
        string stem = Path.Combine(temp.Path, "side");

        SideDatabaseDump.Write(stem, new SideDatabaseMetadata { Observation = "ready", Extents = null });

        using var document = JsonDocument.Parse(File.ReadAllText(stem + ".ready.json"));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("extents").ValueKind);
    }
}
