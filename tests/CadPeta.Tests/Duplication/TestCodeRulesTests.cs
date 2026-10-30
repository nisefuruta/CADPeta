using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using static CadPeta.Tests.Duplication.SourceTree;

namespace CadPeta.Tests.Duplication;

/// <summary>
/// 試験の C# に固有の、同じ補助を何か所にも書かないことの機械検査(本体の形、case の並び、同じ名前の補助は <see cref="CodeDuplicationTests"/>)。
/// 同じ組み立ての式の繰り返し、一時フォルダの作成、mm とインチの換算の直書きを調べる。
/// 対象は 3 つの試験のプロジェクト(xUnit)で、tests/tools の確認用のプログラムは CAD の中で動く別のプログラムなので含めない。
/// </summary>
public sealed class TestCodeRulesTests
{
    private static readonly string[] TestProjects = { "CadPeta.Tests/", "CadPeta.App.Tests/", "CadPeta.Gui.Tests/" };

    /// <summary>3 つの試験のプロジェクトの C# のソース。</summary>
    private static (string Path, string Text)[] UnitTestTexts() =>
        TestTexts().Where(source => TestProjects.Any(project => source.Path.StartsWith(project, StringComparison.Ordinal))).ToArray();

    // ---------------------------------------------------------------- 同じ組み立ての式の繰り返し

    /// <summary>
    /// 同じ式とみなす最小の長さ(空白を除く)。短い式(<c>new IrMove(0, 0)</c> など)は入力の値そのもので、補助にしても読みやすくならない。
    /// </summary>
    private const int MinimumExpressionLength = 40;

    /// <summary>
    /// 繰り返しとみなす最小の回数。この検査は値を組み立てる式(<see cref="IsBuildingExpression"/>)だけを数える。
    /// 2 回から 4 回は、試験ごとに同じ入力を使う自然な繰り返しが多い。5 回以上は補助にまとめられる書き写しなので 5 にした。
    /// </summary>
    private const int MinimumRepeats = 5;

    /// <summary>同じ式を繰り返してよい式と、その理由(キーは空白を除いた式の文字列)。</summary>
    private static readonly Dictionary<string, string> AllowedRepeatedExpressions = new();

    /// <summary>空白を除いた式の文字列。</summary>
    internal static string Compact(SyntaxNode node) => string.Concat(node.DescendantTokens().Select(token => token.Text));

    /// <summary>
    /// 値を組み立てる式か。オブジェクトの作成と、別の呼び出しやオブジェクトの作成を含む呼び出し(<c>new A(x).List()</c>、<c>F(G(x))</c>)。
    /// 値を使わない式文(<c>Assert.True(...)</c>)と、呼び出し 1 つだけの式(補助の呼び出し自体)は含めない。
    /// </summary>
    private static bool IsBuildingExpression(SyntaxNode node) =>
        node is ObjectCreationExpressionSyntax || node is ImplicitObjectCreationExpressionSyntax
        || node is InvocationExpressionSyntax && node.Parent is not ExpressionStatementSyntax
            && node.DescendantNodes().Any(inner => inner is InvocationExpressionSyntax || inner is ObjectCreationExpressionSyntax);

    /// <summary>
    /// 値を組み立てる式のうち、空白を除いた文字列が同じで、長さが <paramref name="minimumLength"/> 以上のものが
    /// <paramref name="minimumRepeats"/> 回以上あるものの、式と場所(ファイル:行)。入れ子になった式で、外側の式と同じ回数のものは、外側の式だけを数える。
    /// </summary>
    internal static (string Expression, string[] Places)[] RepeatedExpressions(IEnumerable<(string Path, string Text)> sources, int minimumLength, int minimumRepeats)
    {
        var found = new List<(string Expression, string Place, SyntaxNode Node)>();
        foreach (var source in sources)
        {
            var tree = CSharpSyntaxTree.ParseText(source.Text, new CSharpParseOptions(LanguageVersion.Latest), source.Path);
            foreach (var node in tree.GetRoot().DescendantNodes().Where(IsBuildingExpression))
            {
                var text = Compact(node);
                if (text.Length >= minimumLength) found.Add((text, source.Path + ":" + Place(node), node));
            }
        }
        var counts = found.GroupBy(each => each.Expression, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return found.GroupBy(each => each.Expression, StringComparer.Ordinal)
            .Where(group => group.Count() >= minimumRepeats)
            // 外側の式が同じ回数で繰り返されているなら、内側の式は別に報告しない。
            .Where(group => !group.Any(each => each.Node.Ancestors().Any(ancestor => counts.TryGetValue(Compact(ancestor), out var outer) && outer == group.Count() && Compact(ancestor).Length >= minimumLength)))
            .Select(group => (group.Key, group.Select(each => each.Place).OrderBy(place => place, StringComparer.Ordinal).ToArray()))
            .OrderBy(each => each.Key, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void testsに同じ組み立ての式が5回以上ない()
    {
        var all = RepeatedExpressions(UnitTestTexts(), MinimumExpressionLength, MinimumRepeats);
        var unexpected = all.Where(each => !AllowedRepeatedExpressions.ContainsKey(each.Expression)).ToArray();
        Assert.True(unexpected.Length == 0,
            "同じ組み立ての式が " + MinimumRepeats + " 回以上あります(" + unexpected.Length + " 種類)。試験の補助にまとめてください:\n"
            + string.Join("\n", unexpected.Select(each => each.Expression + " (" + each.Places.Length + " 回) " + string.Join(" ", each.Places.Take(4)))));
        CodeDuplicationTests.AssertAllowedStillFound(AllowedRepeatedExpressions.Keys, all.Select(each => each.Expression));
    }

    [Fact]
    public void 式の繰り返しの検査は空白と改行が違う同じ式を数え短い式と回数が足りない式と外側が同じ回数の内側の式は除く()
    {
        const string repeated = "class A { void F() { var a = new PdfIrAcquirer(TestAssets.ChartFile); }"
            + " void G() { var b = new PdfIrAcquirer(\n TestAssets.ChartFile); }"
            + " void H() { var c = new PdfIrAcquirer(TestAssets.ChartFile ); Other(c); } }";
        var found = RepeatedExpressions(new[] { ("a.cs", repeated) }, 20, 3);
        Assert.Equal("newPdfIrAcquirer(TestAssets.ChartFile)", found.Single().Expression);
        Assert.Equal(3, found.Single().Places.Length);
        // 回数が足りない。
        Assert.Empty(RepeatedExpressions(new[] { ("a.cs", repeated) }, 20, 4));
        // 短い式。
        Assert.Empty(RepeatedExpressions(new[] { ("a.cs", repeated) }, 80, 3));
        // 外側の式が同じ回数なら、内側の式(Make(TestAssets.ChartFile))は別に報告しない。
        const string nested = "class A { void F() { var a = Wrap(Make(TestAssets.ChartFile)); } void G() { var b = Wrap(Make(TestAssets.ChartFile)); } void H() { var c = Wrap(Make(TestAssets.ChartFile)); } }";
        Assert.Equal("Wrap(Make(TestAssets.ChartFile))", RepeatedExpressions(new[] { ("a.cs", nested) }, 20, 3).Single().Expression);
        // 式文(値を使わない呼び出し)と、呼び出し 1 つだけの式は、組み立てた式として数えない。
        const string statements = "class A { void F() { Assert.True(Make(TestAssets.ChartFile)); } void G() { Assert.True(Make(TestAssets.ChartFile)); } void H() { Assert.True(Make(TestAssets.ChartFile)); } }";
        Assert.Empty(RepeatedExpressions(new[] { ("a.cs", statements) }, 20, 3));
        const string single = "class A { void F() { var a = Helper.Call(TestAssets.ChartFile, path); } void G() { var b = Helper.Call(TestAssets.ChartFile, path); } void H() { var c = Helper.Call(TestAssets.ChartFile, path); } }";
        Assert.Empty(RepeatedExpressions(new[] { ("a.cs", single) }, 20, 3));
    }

    // ---------------------------------------------------------------- 一時フォルダの作成

    /// <summary>一時の場所を作ってよいファイル(3 つの試験のプロジェクトがリンクして共有する)。</summary>
    private const string TempOwner = "CadPeta.Tests/TempFolder.cs";

    /// <summary>システムの一時フォルダを求める呼び出しと、名前に <c>Guid</c> や <c>Temp</c> を使ってフォルダを作る呼び出しの場所。</summary>
    internal static string[] TemporaryLocations(string text)
    {
        var nodes = ParseVariants(text).SelectMany(root => root.DescendantNodes()).ToArray();
        var names = new[] { "GetTempPath", "GetTempFileName", "GetRandomFileName" };
        var temp = nodes.OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access && names.Contains(access.Name.Identifier.Text));
        var created = nodes.OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access && access.Name.Identifier.Text == "CreateDirectory"
                           && (call.ArgumentList.ToString().Contains("Guid") || call.ArgumentList.ToString().Contains("Temp")));
        return temp.Concat(created).Select(call => Place(call)).Distinct().ToArray();
    }

    [Fact]
    public void testsの一時フォルダはTempFolderでだけ作る()
    {
        var violations = UnitTestTexts().Where(source => source.Path != TempOwner)
            .SelectMany(source => TemporaryLocations(source.Text).Select(line => source.Path + ":" + line)).ToArray();
        Assert.True(violations.Length == 0, "一時の場所を " + TempOwner + " の外で作っています(" + violations.Length + " か所)。TempFolder を使ってください:\n" + string.Join("\n", violations));
    }

    // 規則の検査は #if の中のコードも読む（試験は net10 だけのコードを #if NET10_0_OR_GREATER に、src は製品ごとのコードを
    // #if CADPETA_ACAD などに書く。記号を有効にせずに読むと、その中を検査しないまま合格する）
    [Fact]
    public void 規則の検査はifの中と両方の分岐のコードを読む()
    {
        Assert.Single(TemporaryLocations("class A {\n#if NET10_0_OR_GREATER\n string a = Path.GetTempPath();\n#endif\n}"));
        Assert.Equal(new[] { "3", "5" }, TemporaryLocations("class A {\n#if CADPETA_ACAD\n string a = Path.GetTempPath();\n#else\n string b = Path.GetTempPath();\n#endif\n}").OrderBy(p => p, StringComparer.Ordinal));
        Assert.Equal(new[] { "4" }, CodeDuplicationTests.Utf8EncodingCreations("class A {\n#if CADPETA_ARES\n#elif CADPETA_BRICSCAD\n var e = new UTF8Encoding(false);\n#endif\n}"));
    }

    [Fact]
    public void 一時フォルダの検査はGetTempPathとGuidを使うフォルダの作成を見つけ普通のフォルダの作成は除く()
    {
        Assert.Single(TemporaryLocations("class A { string a = Path.GetTempPath(); }"));
        Assert.Single(TemporaryLocations("class A { string a = System.IO.Path.GetTempFileName(); }"));
        Assert.Single(TemporaryLocations("class A { void F() { Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString(\"N\"))); } }"));
        Assert.Single(TemporaryLocations("class A { void F() { Directory.CreateDirectory(TempRoot); } }"));
        Assert.Empty(TemporaryLocations("class A { void F() { Directory.CreateDirectory(Path.Combine(folder.Path, \"sub\")); } }"));
    }

    // ---------------------------------------------------------------- mm とインチの換算

    /// <summary>
    /// 試験で mm とインチ・ピクセルの換算を書いてよいファイル。試験の期待値は、試験される <c>Units</c> を使わずに独立に計算する
    /// (使うと <c>Units</c> の誤りが期待値にも入る)。その換算を 1 か所にまとめる。
    /// </summary>
    private const string InchOwner = "CadPeta.Tests/TestUnits.cs";

    /// <summary>25.4 を掛けるか割る式の場所(座標の値として 25.4 を書いた入力は含めない)。</summary>
    internal static string[] InchConversions(string text) =>
        ParseVariants(text).SelectMany(root => root.DescendantNodes()).OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.NumericLiteralExpression) && Convert.ToDouble(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture) == 25.4)
            .Where(literal => literal.Parent is BinaryExpressionSyntax binary && (binary.IsKind(SyntaxKind.MultiplyExpression) || binary.IsKind(SyntaxKind.DivideExpression)))
            .Select(literal => Place(literal))
            .Distinct().ToArray();

    [Fact]
    public void testsでmmとインチの換算はTestUnitsにだけ書く()
    {
        var violations = UnitTestTexts().Where(source => source.Path != InchOwner)
            .SelectMany(source => InchConversions(source.Text).Select(line => source.Path + ":" + line)).ToArray();
        Assert.True(violations.Length == 0, "25.4 による換算が " + InchOwner + " の外にあります(" + violations.Length + " か所)。TestUnits を使ってください:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void 試験のインチの換算の検査は掛け算と割り算を見つけ座標の値は除く()
    {
        Assert.Single(InchConversions("class A { double a = 50 * 96 / 25.4; }"));
        Assert.Single(InchConversions("class A { double a = x * 25.4; }"));
        Assert.Empty(InchConversions("class A { object a = new IrLine(25.4, 0); void F() { Assert.Equal(25.4, w, 3); } }"));
    }
}
