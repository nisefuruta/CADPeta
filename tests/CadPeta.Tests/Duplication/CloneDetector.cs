using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CadPeta.Tests.Duplication;

/// <summary>
/// 同じ目的のコードが 2 か所以上にあることを見つける検出器。メソッドなどの本体を、識別子とリテラルを除いた形に正規化して比べる。
/// </summary>
internal static class CloneDetector
{
    /// <summary>本体 1 つ。<see cref="Place"/> は「ファイル:行」、<see cref="Member"/> は「型.メンバー」、<see cref="Shape"/> は正規化した形。</summary>
    internal sealed record Body(string Place, string Member, string Shape, int Statements);

    /// <summary>switch の 1 つの分岐に並んだ case のラベル 1 組。<see cref="Labels"/> は名前を並べ替えてコンマでつないだもの。</summary>
    internal sealed record LabelSet(string Place, string Member, string Labels);

    /// <summary>static メソッド 1 つ。<see cref="Signature"/> は「名前(引数の型, …)」。</summary>
    internal sealed record Helper(string Place, string File, string Signature);

    private static SyntaxTree ParseTree(string path, string text) =>
        CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest), path);

    private static string Place(SyntaxTree tree, SyntaxNode node, string path) =>
        path + ":" + (tree.GetLineSpan(node.Span).StartLinePosition.Line + 1);

    private static SyntaxNode? BodyOf(SyntaxNode node) => node switch
    {
        // 値を保持するだけの代入の列で、同じ形でも目的は同じではないので、コンストラクターは比べない。
        ConstructorDeclarationSyntax => null,
        BaseMethodDeclarationSyntax method => (SyntaxNode?)method.Body ?? method.ExpressionBody,
        AccessorDeclarationSyntax accessor => (SyntaxNode?)accessor.Body ?? accessor.ExpressionBody,
        LocalFunctionStatementSyntax local => (SyntaxNode?)local.Body ?? local.ExpressionBody,
        _ => null,
    };

    private static string OwnerOf(SyntaxNode node)
    {
        var type = node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault();
        return type == null ? "?" : type.Identifier.Text;
    }

    private static string NameOf(SyntaxNode node)
    {
        var owner = OwnerOf(node);
        return node switch
        {
            MethodDeclarationSyntax method => owner + "." + method.Identifier.Text,
            AccessorDeclarationSyntax accessor =>
                owner + "." + (accessor.Parent?.Parent is PropertyDeclarationSyntax property ? property.Identifier.Text : "this[]") + "." + accessor.Keyword.Text,
            LocalFunctionStatementSyntax local => owner + "." + local.Identifier.Text,
            _ => owner + "." + node.Kind(),
        };
    }

    /// <summary>
    /// 本体の形。識別子は <c>I</c>、文字列・文字・数値のリテラルは <c>L</c> に置き換え、キーワードと記号はそのまま残す。コメントと空白は含めない。
    /// </summary>
    internal static string Shape(SyntaxNode body)
    {
        var text = new StringBuilder();
        foreach (var token in body.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken)) text.Append('I');
            else if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.CharacterLiteralToken) || token.IsKind(SyntaxKind.NumericLiteralToken)
                     || token.IsKind(SyntaxKind.InterpolatedStringTextToken)) text.Append('L');
            else text.Append(token.Text);
            text.Append(' ');
        }
        return text.ToString();
    }

    /// <summary>引数などを確かめて投げるだけの <c>if (…) throw …;</c>。どのメソッドにもある定型で、処理の大きさに数えない。</summary>
    private static bool IsGuard(StatementSyntax statement) =>
        statement is IfStatementSyntax { Else: null } guard
        && (guard.Statement is ThrowStatementSyntax || guard.Statement is BlockSyntax { Statements: { Count: 1 } block } && block[0] is ThrowStatementSyntax);

    /// <summary>ブロックと引数を確かめるだけの <c>if</c> を除いた、文の数。式形式の本体は 1 と数える。</summary>
    internal static int StatementCount(SyntaxNode body) =>
        body is ArrowExpressionClauseSyntax
            ? 1
            : body.DescendantNodesAndSelf().OfType<StatementSyntax>()
                .Count(statement => statement is not BlockSyntax && !statement.AncestorsAndSelf().OfType<StatementSyntax>().Any(IsGuard));

    internal static Body[] Bodies(string path, string text)
    {
        var tree = ParseTree(path, text);
        var result = new List<Body>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var body = BodyOf(node);
            if (body == null) continue;
            result.Add(new Body(Place(tree, node, path), NameOf(node), Shape(body), StatementCount(body)));
        }
        return result.ToArray();
    }

    /// <summary>形が同じ本体の組(2 つ以上で、文が <paramref name="minimumStatements"/> 以上のもの)。</summary>
    internal static Body[][] SameBodies(IEnumerable<(string Path, string Text)> sources, int minimumStatements) =>
        sources.SelectMany(source => Bodies(source.Path, source.Text))
            .Where(body => body.Statements >= minimumStatements)
            .GroupBy(body => body.Shape, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .Select(group => group.OrderBy(body => body.Place, StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0].Place, StringComparer.Ordinal)
            .ToArray();

    /// <summary>続けて並んだ文の列 1 つ。<see cref="Member"/> は列を含む本体の「型.メンバー」、<see cref="MemberPlace"/> はその本体の「ファイル:行」、<see cref="BodyShape"/> はその本体全体の形、<see cref="Block"/> は列が並ぶブロックの「ファイル:行:桁」、<see cref="Start"/> はブロックの中の先頭の文の位置。</summary>
    internal sealed record Run(string Place, string Member, string MemberPlace, string BodyShape, string Shape, string Block, int Start);

    /// <summary>
    /// 値や対象を変えて同じ操作を並べた一覧か。同じ形の文が 2 つ以上ある(<c>x.Children.Add(a); x.Children.Add(b);</c>)か、
    /// 同じ名前の関数を呼ぶだけの文が 2 つ以上ある(<c>x.SetValue(A, 1); x.SetValue(B, 2); Bind(x, C, "d");</c>)並び。
    /// 一覧は 1 つの手順ではなく、別の一覧と偶然同じ形になる。
    /// </summary>
    private static bool IsList(StatementSyntax[] window)
    {
        var shapes = window.Select(Shape).ToArray();
        if (shapes.Distinct(StringComparer.Ordinal).Count() < shapes.Length) return true;
        var calls = window.OfType<ExpressionStatementSyntax>().Select(statement => statement.Expression as InvocationExpressionSyntax)
            .Where(invocation => invocation != null)
            .Select(invocation => invocation!.Expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.Text : invocation.Expression.ToString())
            .ToArray();
        return calls.Distinct(StringComparer.Ordinal).Count() < calls.Length;
    }

    /// <summary>
    /// ブロックと switch の分岐の中で続けて並んだ <paramref name="length"/> 個の文の列(形のトークンが <paramref name="minimumTokens"/> 以上のもの)。
    /// 本体の全体ではなく一部が同じ手順(メソッドの途中に書き写した手順)を見つける。
    /// </summary>
    internal static Run[] StatementRuns(string path, string text, int length, int minimumTokens)
    {
        var tree = ParseTree(path, text);
        var result = new List<Run>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            var statements = node is BlockSyntax block ? block.Statements : node is SwitchSectionSyntax section ? section.Statements : default;
            if (statements.Count < length) continue;
            var member = node.AncestorsAndSelf().FirstOrDefault(ancestor => BodyOf(ancestor) != null);
            if (member == null) continue;
            var memberPlace = Place(tree, member, path);
            var bodyShape = Shape(BodyOf(member)!);
            for (var start = 0; start + length <= statements.Count; start++)
            {
                var window = statements.Skip(start).Take(length).ToArray();
                // 引数を確かめて投げるだけの if は、どのメソッドにもある定型なので、列に含めない。
                if (window.Any(IsGuard)) continue;
                // 同じ操作を並べた一覧は、1 つの手順ではない。
                if (IsList(window)) continue;
                if (window.Sum(statement => statement.DescendantTokens().Count()) < minimumTokens) continue;
                result.Add(new Run(Place(tree, window[0], path), NameOf(member), memberPlace, bodyShape, string.Concat(window.Select(Shape)),
                    path + ":" + tree.GetLineSpan(node.Span).StartLinePosition, start));
            }
        }
        return result.ToArray();
    }

    /// <summary>
    /// 形が同じ文の列が 2 つ以上の本体にある組(場所の順)。同じ本体の中だけの繰り返しと、列を含む本体の全体の形がすべて同じ組
    /// (本体全体が同じ組として <see cref="SameBodies"/> が見つけるもの)は含めない。
    /// 同じ手順が <paramref name="length"/> より長く続くときは、1 つ前の位置から始まる列が同じ組でも見つかるので、先頭の位置の組だけを返す。
    /// </summary>
    internal static Run[][] SameRuns(IEnumerable<(string Path, string Text)> sources, int length, int minimumTokens)
    {
        var groups = sources.SelectMany(source => StatementRuns(source.Path, source.Text, length, minimumTokens))
            .GroupBy(run => run.Shape, StringComparer.Ordinal)
            .Where(group => group.Select(run => run.MemberPlace).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Where(group => group.Select(run => run.BodyShape).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(group => group.OrderBy(run => run.Place, StringComparer.Ordinal).ToArray())
            .ToArray();
        var groupAt = new Dictionary<(string, int), int>();
        for (var i = 0; i < groups.Length; i++)
        {
            foreach (var run in groups[i]) groupAt[(run.Block, run.Start)] = i;
        }
        return groups
            .Where(group => group.Select(run => groupAt.TryGetValue((run.Block, run.Start - 1), out var before) ? before : -1).Distinct().Count() != 1
                            || group.Any(run => !groupAt.ContainsKey((run.Block, run.Start - 1))))
            .OrderBy(group => group[0].Place, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>続けて並んだ「条件が成り立てば戻る if」の列 1 つ。<see cref="Shape"/> は条件の形だけを並べたもの。</summary>
    internal sealed record ConditionChain(string Place, string Member, string Shape);

    /// <summary>
    /// 条件の形。引数や局所変数の名前は <c>I</c> に置き換え、型・メンバー・呼び出す関数の名前(<c>A.B</c> の A と B)とリテラルは残す。
    /// 戻す値の作り方が違っても、同じ判定を同じ順で並べた手順を見つけるため、条件だけを比べる。
    /// リテラルを残すのは、別の値の集合を対応させる if の列を同じ判定とみなさないため。
    /// </summary>
    internal static string ConditionShape(ExpressionSyntax condition)
    {
        var text = new StringBuilder();
        foreach (var token in condition.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                text.Append(token.Parent is IdentifierNameSyntax name && name.Parent is MemberAccessExpressionSyntax ? token.Text : "I");
            }
            else text.Append(token.Text);
            text.Append(' ');
        }
        return text.ToString();
    }

    /// <summary>else の無い if で、本体が return 1 つ(またはブロックの最後が return)のもの。引数の確認は含めない。</summary>
    private static bool IsEarlyReturn(StatementSyntax statement) =>
        statement is IfStatementSyntax { Else: null } candidate && !IsGuard(statement)
        && (candidate.Statement is ReturnStatementSyntax || candidate.Statement is BlockSyntax { Statements: { Count: > 0 } inner } && inner[inner.Count - 1] is ReturnStatementSyntax);

    /// <summary>ブロックの中で続けて並んだ <paramref name="length"/> 個の「条件が成り立てば戻る if」の列。</summary>
    internal static ConditionChain[] ConditionChains(string path, string text, int length)
    {
        var tree = ParseTree(path, text);
        var result = new List<ConditionChain>();
        foreach (var block in tree.GetRoot().DescendantNodes().OfType<BlockSyntax>())
        {
            var member = block.AncestorsAndSelf().FirstOrDefault(ancestor => BodyOf(ancestor) != null);
            if (member == null) continue;
            var statements = block.Statements;
            for (var start = 0; start + length <= statements.Count; start++)
            {
                var window = statements.Skip(start).Take(length).ToArray();
                if (!window.All(IsEarlyReturn)) continue;
                result.Add(new ConditionChain(Place(tree, window[0], path), NameOf(member),
                    string.Join("| ", window.Select(statement => ConditionShape(((IfStatementSyntax)statement).Condition)))));
            }
        }
        return result.ToArray();
    }

    /// <summary>条件の形が同じ列が 2 つ以上の本体にある組(場所の順)。</summary>
    internal static ConditionChain[][] SameConditionChains(IEnumerable<(string Path, string Text)> sources, int length) =>
        sources.SelectMany(source => ConditionChains(source.Path, source.Text, length))
            .GroupBy(chain => chain.Shape, StringComparer.Ordinal)
            .Where(group => group.Select(chain => chain.Member).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(group => group.OrderBy(chain => chain.Place, StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0].Place, StringComparer.Ordinal)
            .ToArray();

    /// <summary>
    /// switch の 1 つの分岐に並んだ case のラベル(<paramref name="minimumCount"/> 個以上)。
    /// <c>case A: case B: case C: case D: return …;</c> や <c>A or B or C or D =&gt; …</c> のような分類の並びで、
    /// 同じ(またはほとんど同じ)組が別の場所にあれば、同じ分類を 2 か所で書いている。
    /// <paramref name="wholeSwitch"/> が true なら、switch 全体のラベル(分岐ごとに 1 つずつ並べた値の集合)を読む。
    /// </summary>
    internal static LabelSet[] LabelSets(string path, string text, int minimumCount, bool wholeSwitch)
    {
        var tree = ParseTree(path, text);
        var result = new List<LabelSet>();
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            IEnumerable<string>? labels = null;
            if (wholeSwitch)
            {
                if (node is SwitchStatementSyntax statement) labels = statement.Sections.SelectMany(each => each.Labels.OfType<CaseSwitchLabelSyntax>()).Select(label => label.Value.ToString());
                else if (node is SwitchExpressionSyntax expression) labels = expression.Arms.SelectMany(each => ConstantPatterns(each.Pattern));
            }
            else if (node is SwitchSectionSyntax section) labels = section.Labels.OfType<CaseSwitchLabelSyntax>().Select(label => label.Value.ToString());
            else if (node is SwitchExpressionArmSyntax arm) labels = ConstantPatterns(arm.Pattern);
            if (labels == null) continue;
            // 型名つき(Kind.A)と型名なし(A)の両方で書かれうるので、最後の名前だけで比べる。
            var names = labels.Select(label => label.Substring(label.LastIndexOf('.') + 1)).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal).ToArray();
            if (names.Length < minimumCount) continue;
            var member = node.Ancestors().FirstOrDefault(ancestor => BodyOf(ancestor) != null);
            result.Add(new LabelSet(Place(tree, node, path), member == null ? OwnerOf(node) : NameOf(member), string.Join(",", names)));
        }
        return result.ToArray();
    }

    private static IEnumerable<string> ConstantPatterns(PatternSyntax pattern) => pattern switch
    {
        ConstantPatternSyntax constant => new[] { constant.Expression.ToString() },
        BinaryPatternSyntax binary when binary.IsKind(SyntaxKind.OrPattern) => ConstantPatterns(binary.Left).Concat(ConstantPatterns(binary.Right)),
        _ => Array.Empty<string>(),
    };

    /// <summary>2 つのラベルの組が「ほとんど同じ」か(共通が <paramref name="minimumCount"/> 個以上で、共通の数が和集合の 4 分の 3 以上)。</summary>
    internal static bool SimilarLabels(LabelSet a, LabelSet b, int minimumCount)
    {
        var left = new HashSet<string>(a.Labels.Split(','), StringComparer.Ordinal);
        var right = new HashSet<string>(b.Labels.Split(','), StringComparer.Ordinal);
        var common = left.Intersect(right).Count();
        var union = left.Union(right).Count();
        return common >= minimumCount && common * 4 >= union * 3;
    }

    /// <summary>ほとんど同じラベルの組でつながる場所の集まり(2 か所以上。場所の順)。</summary>
    internal static LabelSet[][] SimilarLabelSets(IEnumerable<(string Path, string Text)> sources, int minimumCount, bool wholeSwitch)
    {
        var all = sources.SelectMany(source => LabelSets(source.Path, source.Text, minimumCount, wholeSwitch)).ToArray();
        var cluster = Enumerable.Range(0, all.Length).ToArray();
        int Find(int i) => cluster[i] == i ? i : cluster[i] = Find(cluster[i]);
        for (var i = 0; i < all.Length; i++)
        {
            for (var j = i + 1; j < all.Length; j++)
            {
                if (SimilarLabels(all[i], all[j], minimumCount)) cluster[Find(j)] = Find(i);
            }
        }
        return Enumerable.Range(0, all.Length)
            .GroupBy(Find)
            .Where(group => group.Count() >= 2)
            .Select(group => group.Select(i => all[i]).OrderBy(set => set.Place, StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0].Place, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// 試験の補助のように使う static メソッド(Fact / Theory のメソッドは含めない)を、名前と引数の型で読む。
    /// インスタンスのメソッドは、同じインターフェイスを実装する偽物などが同じ名前になるので含めない。
    /// Win32 の関数の宣言(extern)は本体を持たず、使うアセンブリごとに internal で宣言するものなので含めない。
    /// 自分の型を返す作成の関数も含めない(<see cref="ReturnsOwnType"/>)。
    /// </summary>
    internal static Helper[] StaticHelpers(string path, string text)
    {
        var tree = ParseTree(path, text);
        return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => method.Modifiers.Any(SyntaxKind.StaticKeyword) && !method.Modifiers.Any(SyntaxKind.ExternKeyword))
            .Where(method => !(method.Parent is BaseTypeDeclarationSyntax owner && ReturnsOwnType(method, owner)))
            .Where(method => !method.AttributeLists.SelectMany(list => list.Attributes)
                .Any(attribute => attribute.Name.ToString().StartsWith("Fact", StringComparison.Ordinal) || attribute.Name.ToString().StartsWith("Theory", StringComparison.Ordinal)))
            .Select(method => new Helper(Place(tree, method, path), path,
                method.Identifier.Text + "(" + string.Join(", ", method.ParameterList.Parameters.Select(parameter => parameter.Type?.ToString())) + ")"))
            .ToArray();
    }

    /// <summary>自分の型を返す作成の関数(<c>Result.Failed</c> など)か。型ごとに同じ役割の作成の関数を持つので名前が同じになり、作るものは別の型である。</summary>
    private static bool ReturnsOwnType(MethodDeclarationSyntax method, BaseTypeDeclarationSyntax owner) =>
        method.ReturnType.ToString() == owner.Identifier.Text || method.ReturnType.ToString() == owner.Identifier.Text + "?";

    /// <summary>同じ名前と引数の型の static メソッドが 2 つ以上のファイルにある組(場所の順)。同じ目的の補助を別のファイルに書き写すと、名前も同じになる。</summary>
    internal static Helper[][] SameNameHelpers(IEnumerable<(string Path, string Text)> sources) =>
        sources.SelectMany(source => StaticHelpers(source.Path, source.Text))
            .GroupBy(helper => helper.Signature, StringComparer.Ordinal)
            .Where(group => group.Select(helper => helper.File).Distinct(StringComparer.Ordinal).Count() >= 2)
            .Select(group => group.OrderBy(helper => helper.Place, StringComparer.Ordinal).ToArray())
            .OrderBy(group => group[0].Place, StringComparer.Ordinal)
            .ToArray();
}
