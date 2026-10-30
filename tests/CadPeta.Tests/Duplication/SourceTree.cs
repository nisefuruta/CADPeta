using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CadPeta.Tests.Duplication;

/// <summary>リポジトリのソースを文字列として読む重複の検査で共有する補助。</summary>
internal static class SourceTree
{

    /// <summary>フォルダの下(サブフォルダを含む)で、パターンに合うファイルを返す。<c>bin</c> と <c>obj</c> の下は除く。</summary>
    public static string[] Files(string directory, string pattern) =>
        Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(directory, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part == "bin" || part == "obj"))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

    public static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary><c>src</c> の C# のソースと、<c>src</c> からの相対パス。</summary>
    public static (string Path, string Text)[] SrcTexts() => CSharpTexts("src");

    /// <summary><c>tests</c> の C# のソース(3 つの試験のプロジェクト)と、<c>tests</c> からの相対パス。</summary>
    public static (string Path, string Text)[] TestTexts() => CSharpTexts("tests");

    private static (string Path, string Text)[] CSharpTexts(string folder)
    {
        var root = Path.Combine(RepositoryPaths.Root(), folder);
        return Files(root, "*.cs").Select(path => (Relative(root, path), File.ReadAllText(path))).ToArray();
    }

    /// <summary>C# のソースの構文木の根（条件付きコンパイルの記号は 1 つも有効にしない）。</summary>
    public static SyntaxNode Parse(string text) => Parse(text, Array.Empty<string>());

    private static SyntaxNode Parse(string text, IEnumerable<string> symbols) =>
        CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: symbols)).GetRoot();

    /// <summary>
    /// 条件付きコンパイルのどの分岐のコードも読めるように、記号を 1 つも有効にしない構文木と、<c>#if</c>・<c>#elif</c> に現れる記号を
    /// 1 つずつ有効にした構文木を返す（試験は net10 だけのコードを <c>#if NET10_0_OR_GREATER</c> に、src は製品ごとのコードを
    /// <c>#if CADPETA_ACAD</c> などに書くので、記号を有効にせずに読むとその中を検査しない）。同じ場所は複数の構文木に現れるので、
    /// 呼び出し側は結果の場所をまとめる。
    /// </summary>
    public static SyntaxNode[] ParseVariants(string text)
    {
        var symbols = Regex.Matches(text, @"^[ \t]*#[ \t]*(?:if|elif)\b(?<expr>[^\r\n]*)", RegexOptions.Multiline)
            .SelectMany(m => Regex.Matches(m.Groups["expr"].Value, @"[A-Za-z_][A-Za-z0-9_]*").Select(s => s.Value))
            .Where(s => s != "true" && s != "false")
            .Distinct(StringComparer.Ordinal);
        return new[] { Parse(text) }.Concat(symbols.Select(s => Parse(text, new[] { s }))).ToArray();
    }

    /// <summary>
    /// <c>new</c> で作る式と、作る型の名前。型を省略した <c>new(...)</c> は、変数・フィールド・プロパティの宣言の型を、作る型とみなす
    /// (型の名前で探す検査が、<c>UTF8Encoding a = new(false);</c> を見逃さないため)。
    /// </summary>
    public static IEnumerable<(SyntaxNode Node, string TypeName)> Creations(SyntaxNode root)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (node is ObjectCreationExpressionSyntax creation) yield return (creation, creation.Type.ToString());
            else if (node is ImplicitObjectCreationExpressionSyntax implicitCreation && DeclaredType(implicitCreation) is TypeSyntax declared) yield return (implicitCreation, declared.ToString());
        }
    }

    private static TypeSyntax? DeclaredType(SyntaxNode creation) =>
        creation.Parent is EqualsValueClauseSyntax { Parent: var owner }
            ? owner is VariableDeclaratorSyntax { Parent: VariableDeclarationSyntax declaration } ? declaration.Type : (owner as PropertyDeclarationSyntax)?.Type
            : null;

    /// <summary>構文木の節の行番号(1 から)。</summary>
    public static string Place(SyntaxNode node) =>
        (node.SyntaxTree.GetLineSpan(node.Span).StartLinePosition.Line + 1).ToString(CultureInfo.InvariantCulture);
}
