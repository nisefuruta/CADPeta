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
/// 同じ目的のコードが src と tests の 2 か所以上にないことの機械検査。tests に固有の検査は <see cref="TestCodeRulesTests"/>。
/// 本体の形が同じメソッド、同じ分類の case の並び、同じ値の集合を分岐する switch、同じ名前と引数の static メソッド、
/// UTF-8 の作成、mm とインチの換算を調べる。
/// </summary>
public sealed class CodeDuplicationTests
{
    // ---------------------------------------------------------------- 本体の形が同じメソッド

    /// <summary>
    /// 比べる本体の最小の文の数。実際の検出結果を見て決めた。コンストラクターは値を保持する代入の列で、形が同じでも目的は同じではないので比べない。
    /// 引数を確かめて投げるだけの if も数えない。この 2 つを除いて 3 にすると、検出した組は、いずれも同じ手順の書き写しだった
    /// (4 つの CAD のプラグインの選択・範囲の入力・プロットの設定、2 つの取得の外接矩形の更新、2 つの画面のパスの確かめ方など)。
    /// 4 にすると、3 文の書き写し(PromptString、ReadPickFrame、Publish など)を見逃す。
    /// 2 にすると、警告を 1 行書くだけの関数の組のように、偶然同じ形になる組が出る。
    /// </summary>
    private const int MinimumStatements = 3;

    /// <summary>
    /// 試験の C# で比べる本体の最小の文の数。試験は「入力を作る・処理する・結果を確かめる」の 3 文が、入力の違う別の約束でも同じ形になる
    /// (実際の検出結果で、3 にすると別の約束の試験の組が検出の多くを占め、補助の書き写しが埋もれた)。補助の書き写しは 4 文以上のものを調べる。
    /// </summary>
    private const int MinimumTestStatements = 4;

    private static string Key(IEnumerable<string> members) => string.Join(" / ", members.OrderBy(member => member, StringComparer.Ordinal));

    /// <summary>
    /// 形が同じでも許す本体の組と、その理由。キーは「型.メンバー」を並べ替えて " / " でつないだもの。
    /// 理由を書けない組は許さず、コードを直す。理由が無くなったら(組が 2 か所に出なくなったら)削除する(検査が不合格にする)。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSameBodies = new();

    /// <summary>試験の C# で形が同じでも許す本体の組と、その理由。許すのは、まとめると試験が何を確かめるのか読めなくなるものだけにする。</summary>
    private static readonly Dictionary<string, string> AllowedSameTestBodies = new();

    private static void AssertNoSameBodies((string Path, string Text)[] sources, Dictionary<string, string> allowedBodies, int minimumStatements)
    {
        var groups = CloneDetector.SameBodies(sources, minimumStatements);
        var unexpected = groups.Where(group => !allowedBodies.ContainsKey(Key(group.Select(body => body.Member)))).ToArray();
        Assert.True(unexpected.Length == 0,
            "形が同じ本体が 2 か所以上にあります(" + unexpected.Length + " 組)。1 つの関数にまとめてください:\n"
            + string.Join("\n", unexpected.Select(group => string.Join(" ", group.Select(body => body.Place + " " + body.Member)))));
        AssertAllowedStillFound(allowedBodies.Keys, groups.Select(group => Key(group.Select(body => body.Member))));
    }

    /// <summary>許す一覧の項目が、今のコードでまだ検出されることを確かめる。検出されない項目は一覧から削除させる。</summary>
    internal static void AssertAllowedStillFound(IEnumerable<string> allowed, IEnumerable<string> found)
    {
        var present = found.ToHashSet(StringComparer.Ordinal);
        foreach (var key in allowed)
        {
            Assert.True(present.Contains(key), "許す一覧の「" + key + "」は検出されません。一覧から削除してください。");
        }
    }

    [Fact]
    public void srcに形が同じ本体のメソッドが2か所以上にない()
    {
        AssertNoSameBodies(SrcTexts(), AllowedSameBodies, MinimumStatements);
    }

    [Fact]
    public void testsに形が同じ本体のメソッドが2か所以上にない()
    {
        AssertNoSameBodies(TestTexts(), AllowedSameTestBodies, MinimumTestStatements);
    }

    [Fact]
    public void 形が同じ本体の検査は識別子とリテラルが違う本体を見つけ短い本体とコンストラクターと引数の確認だけの文は除く()
    {
        const string first = "class A { int F(string a) { var x = a.Length; if (x > 1) { return x + 2; } Log(a, \"first\"); return 0; } }";
        const string second = "class B { int G(string b) { var y = b.Length; if (y > 1) { return y + 9; } Out(b, \"second\"); return 1; } }";
        var found = CloneDetector.SameBodies(new[] { ("a.cs", first), ("b.cs", second) }, MinimumStatements);
        Assert.Equal(new[] { "A.F", "B.G" }, found.Single().Select(body => body.Member).ToArray());
        // 構造が違えば別の本体。
        const string different = "class C { int H(string c) { var z = c.Length; while (z > 1) { z--; } Out(c, \"third\"); return 1; } }";
        Assert.Empty(CloneDetector.SameBodies(new[] { ("a.cs", first), ("c.cs", different) }, MinimumStatements));
        // 文が少ない本体は比べない。
        const string shortA = "class A { int F(string a) { var x = a.Length; return x; } }";
        const string shortB = "class B { int G(string b) { var y = b.Length; return y; } }";
        Assert.Empty(CloneDetector.SameBodies(new[] { ("a.cs", shortA), ("b.cs", shortB) }, MinimumStatements));
        // コンストラクターは比べない。
        const string ctorA = "class A { int p, q, r, s; A(int a, int b, int c, int d) { p = a; q = b; r = c; s = d; } }";
        const string ctorB = "class B { int p, q, r, s; B(int a, int b, int c, int d) { p = a; q = b; r = c; s = d; } }";
        Assert.Empty(CloneDetector.SameBodies(new[] { ("a.cs", ctorA), ("b.cs", ctorB) }, MinimumStatements));
        // 引数の確認だけの if は文に数えない(確認を除くと 2 文)。
        const string guardA = "class A { int F(object a) { if (a == null) throw new E(); if (a == null) { throw new E(); } var x = a.Id; return x; } }";
        const string guardB = "class B { int G(object b) { if (b == null) throw new E(); if (b == null) { throw new E(); } var y = b.Id; return y; } }";
        Assert.Equal(2, CloneDetector.Bodies("a.cs", guardA).Single().Statements);
        Assert.Empty(CloneDetector.SameBodies(new[] { ("a.cs", guardA), ("b.cs", guardB) }, MinimumStatements));
    }

    [Fact]
    public void 許す一覧の検査は検出されなくなった項目で不合格になる()
    {
        AssertAllowedStillFound(new[] { "A.F / B.G" }, new[] { "A.F / B.G", "C.H / D.I" });
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => AssertAllowedStillFound(new[] { "A.F / B.G" }, new[] { "C.H / D.I" }));
    }

    // ---------------------------------------------------------------- 本体の一部の同じ文の列

    /// <summary>
    /// src で比べる文の列の長さと、列の最小のトークンの数。本体の全体の比較では、同じ手順を別の手順の前後に書いた本体を見つけられない。
    /// 値は TurnTrace の検出結果で決めたもの(長さ 3・20 トークンで、変数を読むだけの列と設定を並べた一覧を除くと、検出した組はいずれも
    /// 同じ手順の書き写しか、理由のある偶然の一致だった。30 トークンでは短い名前の手順を見逃し、長さ 2 では変数を 2 つ宣言するだけの列が加わる)。
    /// 引数を確かめて投げるだけの if を含む列は比べない。tests では比べない(Assert を並べた列が、確かめる値が違うだけの試験の間で同じ形になる)。
    /// </summary>
    private const int RunLength = 3;

    private const int RunTokens = 20;

    /// <summary>同じ文の列を許す組と、その理由。キーは「型.メンバー」を並べ替えて " / " でつないだもの(つながる場所の全部)。</summary>
    private static readonly Dictionary<string, string> AllowedSameRuns = new()
    {
        [Key(new[] { "GradientRasterizer.Render", "PdfImageDecoder.SaveAsPng" })] =
            "Bitmap を PNG のバイト列にする 3 文(MemoryStream を作り、PNG で保存し、配列にする)。2 つは参照し合わない取得の層(Acquire.Pdf と Acquire.Raster)にあり、"
            + "共通に参照する Acquire.Common は System.Drawing を参照しない層なので、まとめる先に置くと Acquire.Common が System.Drawing に依存する。",
        [Key(new[] { "ShxGlyphWalker.HandleFractionalArc", "ShxGlyphWalker.HandleOctantArc" })] =
            "命令の引数のバイトを順に変数へ読む定型。読むバイトの意味(半径とフラグ / 開始と終了のオフセット)と数が違い、まとめる処理が無い。",
    };

    [Fact]
    public void srcに同じ文の列が2つ以上の関数にない()
    {
        var groups = CloneDetector.SameRuns(SrcTexts(), RunLength, RunTokens);
        var unexpected = groups.Where(group => !AllowedSameRuns.ContainsKey(Key(group.Select(run => run.Member).Distinct()))).ToArray();
        Assert.True(unexpected.Length == 0,
            "同じ文の列が 2 つ以上の関数にあります(" + unexpected.Length + " 組)。1 つの関数にまとめてください:\n"
            + string.Join("\n", unexpected.Select(group => string.Join(" ", group.Select(run => run.Place + " " + run.Member)))));
        AssertAllowedStillFound(AllowedSameRuns.Keys, groups.Select(group => Key(group.Select(run => run.Member).Distinct())));
    }

    [Fact]
    public void 文の列の検査は本体の途中の同じ手順を見つけ短い列と引数の確認と同じ本体の繰り返しと本体全体が同じ組は除く()
    {
        const string first = "class A { void F(D d) { Before(); var e = (X)t.Get(d.Id); var r = M.Create(v); e.SetAt(k, r); After(1); } }";
        const string second = "class B { void G(D o) { var x = (X)t.Get(o.Id); var y = M.Create(w); x.SetAt(n, y); } }";
        var found = CloneDetector.SameRuns(new[] { ("a.cs", first), ("b.cs", second) }, 3, 20);
        Assert.Equal(new[] { "A.F", "B.G" }, found.Single().Select(run => run.Member).ToArray());
        // トークンが少ない列は比べない。
        Assert.Empty(CloneDetector.SameRuns(new[] { ("a.cs", first), ("b.cs", second) }, 3, 200));
        // 文が 2 つの並びは、長さ 3 では比べない。
        const string shortA = "class A { void F(D d) { Before(); var e = (X)t.Get(d.Id); e.SetAt(k, r); After(1); } }";
        const string shortB = "class B { void G(D o) { var x = (X)t.Get(o.Id); x.SetAt(n, y); } }";
        Assert.Empty(CloneDetector.SameRuns(new[] { ("a.cs", shortA), ("b.cs", shortB) }, 3, 1));
        // 引数を確かめる if を含む列は比べない。
        const string guardA = "class A { void F(object a, object b, object c) { if (a == null) throw new E(); if (b == null) throw new E(); if (c == null) throw new E(); } }";
        const string guardB = "class B { void G(object x, object y, object z) { if (x == null) throw new E(); if (y == null) throw new E(); if (z == null) throw new E(); } }";
        Assert.Empty(CloneDetector.SameRuns(new[] { ("a.cs", guardA), ("b.cs", guardB) }, 3, 10));
        // 同じ関数の中だけの繰り返しは、別の関数の重複ではない。
        const string repeated = "class C { void H() { var a = (X)t.Get(i.Id); a.Run(1, 2); a.Stop(3); var b = (X)t.Get(i.Id); b.Run(1, 2); b.Stop(3); } }";
        Assert.Empty(CloneDetector.SameRuns(new[] { ("c.cs", repeated) }, 3, 10));
        // 同じ手順が 4 文以上続くときは、位置をずらした列も同じ組で見つかるが、先頭の位置の 1 組だけを報告する。
        const string longA = "class A { void F(D d) { Before(); var a = Load(d.P); Check(a, 1); Apply(a, d.Q, 2); Save(a); After(1); } }";
        const string longB = "class B { void G(D o) { var a = Load(o.P); Check(a, 1); Apply(a, o.Q, 2); Save(a); } }";
        Assert.Equal(new[] { "A.F", "B.G" }, CloneDetector.SameRuns(new[] { ("a.cs", longA), ("b.cs", longB) }, 3, 1).Single().Select(run => run.Member).ToArray());
        // 同じ形の文や、同じ関数を呼ぶ文が 2 つ以上ある並びは、値を変えて並べた一覧なので比べない。
        const string listA = "class A { void F(X x) { var e = New(typeof(T)); e.Set(P, 1); e.Set(Q, 2); Bind(e, R, \"s\"); } }";
        const string listB = "class B { void G(Y y) { var e = New(typeof(U)); e.Set(P, 3); e.Set(Q, 4); Bind(e, R, \"t\"); After(2); } }";
        Assert.Empty(CloneDetector.SameRuns(new[] { ("a.cs", listA), ("b.cs", listB) }, 3, 1));
        // 本体の全体が同じ組は、本体の形が同じメソッドの検査が見つけるので、文の列としては重ねて報告しない。
        const string wholeA = "class A { void F(D d) { var e = (X)t.Get(d.Id); var r = M.Create(v); e.SetAt(k, r); } }";
        const string wholeB = "class B { void G(D o) { var x = (X)t.Get(o.Id); var y = M.Create(w); x.SetAt(n, y); } }";
        Assert.Single(CloneDetector.SameBodies(new[] { ("a.cs", wholeA), ("b.cs", wholeB) }, MinimumStatements));
        Assert.Empty(CloneDetector.SameRuns(new[] { ("a.cs", wholeA), ("b.cs", wholeB) }, 3, 20));
    }

    // ---------------------------------------------------------------- 同じ判定を同じ順に並べた if の列

    /// <summary>比べる「条件が成り立てば戻る if」の列の長さ。戻す値の作り方が違うと文の形が違うので、文の列の検査では見つからない。</summary>
    private const int ChainLength = 2;

    /// <summary>同じ判定を同じ順に並べた if の列を許す組と、その理由。キーは「型.メンバー」を並べ替えて " / " でつないだもの(つながる場所の全部)。</summary>
    private static readonly Dictionary<string, string> AllowedSameConditionChains = new()
    {
        [Key(new[] { "CadIrAcquirer.ResolveLinetype", "CadIrAcquirer.ResolveLinetypeSymbols" })] =
            "線種が ByBlock か、要素が無い(実線)かの 2 つの判定。ByBlock のとき、破線の長さはブロック挿入側から引き継ぎ、記号は引き継がない(既存の規則)ので、"
            + "戻す値が判定ごとに違う。判定の順を 1 つにしても、呼び出し側でもう一度分岐することになる。",
    };

    [Fact]
    public void srcに同じ判定を同じ順に並べたifの列が2つ以上の関数にない()
    {
        var groups = CloneDetector.SameConditionChains(SrcTexts(), ChainLength);
        var unexpected = groups.Where(group => !AllowedSameConditionChains.ContainsKey(Key(group.Select(chain => chain.Member).Distinct()))).ToArray();
        Assert.True(unexpected.Length == 0,
            "同じ判定を同じ順に並べた if の列が 2 つ以上の関数にあります(" + unexpected.Length + " 組)。判定の順を 1 つの関数にして、結果をそれぞれの型に変えてください:\n"
            + string.Join("\n", unexpected.Select(group => string.Join(" ", group.Select(chain => chain.Place + " " + chain.Member)) + "  条件: " + group[0].Shape)));
        AssertAllowedStillFound(AllowedSameConditionChains.Keys, groups.Select(group => Key(group.Select(chain => chain.Member).Distinct())));
    }

    [Fact]
    public void ifの列の検査は戻す値が違っても同じ判定の並びを見つけ値と呼ぶ関数が違う判定と戻らないifは除く()
    {
        const string first = "class A { string F(string r, string f) { if (Fonts.Contains(r)) return r; if (Fonts.Contains(f)) return f; return null; } }";
        const string second = "class B { Choice G(string a, string b) { if (Fonts.Contains(a)) return new Choice(a, 1); if (Fonts.Contains(b)) { Log(b); return new Choice(b, 2); } return null; } }";
        Assert.Equal(new[] { "A.F", "B.G" }, CloneDetector.SameConditionChains(new[] { ("a.cs", first), ("b.cs", second) }, 2).Single().Select(chain => chain.Member).ToArray());
        // 比べる値が違えば別の判定(別の値の集合を対応させる if の列)。
        const string horizontal = "class C { int H(int v) { if (v == 1) return 0; if (v == -4131) return 1; return 2; } }";
        const string vertical = "class D { int V(int v) { if (v == -4160) return 0; if (v == -4108) return 1; return 2; } }";
        Assert.Empty(CloneDetector.SameConditionChains(new[] { ("c.cs", horizontal), ("d.cs", vertical) }, 2));
        // 呼ぶ関数の名前が違えば別の判定。
        const string other = "class E { string I(string r, string f) { if (Files.Exists(r)) return r; if (Files.Exists(f)) return f; return null; } }";
        Assert.Empty(CloneDetector.SameConditionChains(new[] { ("a.cs", first), ("e.cs", other) }, 2));
        // 戻らない if は列にしない。
        const string notReturning = "class F { void J(string r, string f) { if (Fonts.Contains(r)) Use(r); if (Fonts.Contains(f)) Use(f); } }";
        Assert.Empty(CloneDetector.SameConditionChains(new[] { ("a.cs", first), ("f.cs", notReturning) }, 2));
        // 引数の確認(投げるだけの if)は列にしない。
        const string guardA = "class G { void K(object a, object b) { if (a == null) throw new E(); if (b == null) throw new E(); } }";
        const string guardB = "class H { void L(object a, object b) { if (a == null) throw new E(); if (b == null) throw new E(); } }";
        Assert.Empty(CloneDetector.SameConditionChains(new[] { ("g.cs", guardA), ("h.cs", guardB) }, 2));
    }

    // ---------------------------------------------------------------- 有限の判定

    /// <summary>有限の判定(<c>double.IsNaN</c>、<c>double.IsInfinity</c>、<c>double.IsFinite</c>)を書いてよいファイル(判定の原本)。</summary>
    private const string FiniteOwner = "CadPeta.Core/Geometry/Numbers.cs";

    /// <summary>
    /// 原本の外で書いてよい有限の判定と、その理由。キーは「ファイル:メンバー」。有限かどうかの判定は <c>Numbers.IsFinite</c> を使う。
    /// 理由が無くなったら(判定が無くなったら)削除する(検査が不合格にする)。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedFiniteChecks = new()
    {
        ["CadPeta.Core/Geometry/GeoClip.cs:EmitLine"] = "直前の終点が無いことを NaN で表す番兵の判定。値が有限かの判定ではない。",
        ["CadPeta.Core/Geometry/GeoClip.cs:ClipStroke"] = "直前の終点が無いことを NaN で表す番兵の判定。値が有限かの判定ではない。",
        ["CadPeta.Plugin.Shared/PlotOrchestrator.cs:DecideWindowFit"] = "PaperScaleCalculator が「明示の尺度を定義できない」ことを NaN で返す番兵の判定。値が有限かの判定ではない。",
        ["CadPeta.Plugin.Shared/PlotOrchestrator.cs:ComputeWindowCropRect"] = "PaperScaleCalculator が「明示の尺度を定義できない」ことを NaN で返す番兵の判定。値が有限かの判定ではない。",
    };

    /// <summary><c>double.IsNaN</c>、<c>double.IsInfinity</c>、<c>double.IsFinite</c> の呼び出しの、含むメンバーの名前。</summary>
    internal static string[] FiniteChecks(string text) =>
        ParseVariants(text).SelectMany(root => root.DescendantNodes()).OfType<InvocationExpressionSyntax>()
            .Where(call => call.Expression is MemberAccessExpressionSyntax access
                           && (access.Name.Identifier.Text == "IsNaN" || access.Name.Identifier.Text == "IsInfinity" || access.Name.Identifier.Text == "IsFinite")
                           && (access.Expression.ToString() == "double" || access.Expression.ToString() == "Double" || access.Expression.ToString() == "System.Double"))
            .Select(call => call.Ancestors().Select(ancestor => ancestor switch
            {
                MethodDeclarationSyntax method => method.Identifier.Text,
                LocalFunctionStatementSyntax local => local.Identifier.Text,
                PropertyDeclarationSyntax property => property.Identifier.Text,
                ConstructorDeclarationSyntax constructor => constructor.Identifier.Text,
                _ => null,
            }).FirstOrDefault(name => name != null) ?? "?")
            .Distinct().ToArray();

    [Fact]
    public void srcの有限の判定はNumbersの1か所にある()
    {
        var found = SrcTexts().Where(source => source.Path != FiniteOwner)
            .SelectMany(source => FiniteChecks(source.Text).Select(member => source.Path + ":" + member)).ToArray();
        var unexpected = found.Where(each => !AllowedFiniteChecks.ContainsKey(each)).Distinct().ToArray();
        Assert.True(unexpected.Length == 0,
            "double.IsNaN / IsInfinity / IsFinite の判定が " + FiniteOwner + " の外にあります(" + unexpected.Length + " か所)。Numbers.IsFinite を使ってください:\n" + string.Join("\n", unexpected));
        AssertAllowedStillFound(AllowedFiniteChecks.Keys, found);
    }

    [Fact]
    public void 有限の判定の検査はdoubleの3つの判定を含むメンバーの名前で見つけ別の型と別の判定は除く()
    {
        Assert.Equal(new[] { "A", "P", "F" }, FiniteChecks("class A { A() { var a = Double.IsFinite(1); } int P { get { return double.IsNaN(2) ? 1 : 0; } } bool F(double v) => System.Double.IsInfinity(v); }"));
        Assert.Equal(new[] { "Local" }, FiniteChecks("class A { void F() { bool Local(double v) => double.IsNaN(v); } }"));
        Assert.Empty(FiniteChecks("class A { bool F(double v) => Numbers.IsFinite(v) || double.IsPositiveInfinity(v) || float.IsNaN(1f) || Other.IsNaN(v); }"));
    }

    // ---------------------------------------------------------------- 同じ分類の case の並び

    /// <summary>同じ分類の並びとみなす、case のラベルの最小の数。3 個以下の並び(真偽や 2 方向の分岐)は、偶然に同じ組になる。</summary>
    private const int MinimumLabels = 4;

    /// <summary>同じ分類の case の並び(1 つの分岐に重ねた case)を許す組と、その理由。キーは「型.メンバー」を並べ替えて " / " でつないだもの。</summary>
    private static readonly Dictionary<string, string> AllowedSameLabels = new();

    /// <summary>同じ値の集合を同じように並べた switch 全体を許す組と、その理由。キーは「型.メンバー」を並べ替えて " / " でつないだもの。</summary>
    private static readonly Dictionary<string, string> AllowedSameSwitches = new()
    {
        ["ArtifactFile.Validate / PdfPetaPipeline.Run / PdfPetaValidation.ExpectedExtension"] =
            "同じ出力形式(EMF・PDF・PNG・SVG)を分岐するが、Run は形式ごとの書き出し(App)、ExpectedExtension は入力の検査の拡張子(App)、"
            + "Validate は書き出したファイルの中身の検査(UseCases)を選ぶ。分岐した先の処理が別々の層と目的にあり、1 つの表にすると UseCases が "
            + "App の書き出しを知るか、App が中身の検査を持つことになる。",
        ["PdfPngDecoder.Decode"] =
            "1 つの関数の中で、PNG の色の種類(5 種)から 1 画素の成分の数を求める switch と、画素を RGBA へ変換する switch。"
            + "前者は行の長さの計算に、後者は画素ごとの変換に使い、1 つにまとめると画素ごとの処理に行の長さの計算が混ざる。",
    };

    private static readonly Dictionary<string, string> AllowedSameTestSwitches = new();

    private static readonly Dictionary<string, string> NoAllowed = new();

    private static void AssertNoSimilarLabels((string Path, string Text)[] sources, bool wholeSwitch, Dictionary<string, string> allowedPairs, string advice)
    {
        var pairs = CloneDetector.SimilarLabelSets(sources, MinimumLabels, wholeSwitch);
        var unexpected = pairs.Where(pair => !allowedPairs.ContainsKey(Key(pair.Select(set => set.Member).Distinct()))).ToArray();
        Assert.True(unexpected.Length == 0,
            advice + "(" + unexpected.Length + " 組):\n" + string.Join("\n", unexpected.Select(pair => string.Join(" ", pair.Select(set => set.Place + " " + set.Member)))));
        AssertAllowedStillFound(allowedPairs.Keys, pairs.Select(pair => Key(pair.Select(set => set.Member).Distinct())));
    }

    [Fact]
    public void srcに同じ分類のcaseの並びが2か所以上にない()
    {
        AssertNoSimilarLabels(SrcTexts(), false, AllowedSameLabels, "同じ分類の case の並びが 2 か所以上にあります。分類を 1 つの関数にして、そこから使ってください");
    }

    [Fact]
    public void srcに同じ値の集合を分岐するswitchが2か所以上にない()
    {
        AssertNoSimilarLabels(SrcTexts(), true, AllowedSameSwitches, "同じ値の集合を分岐する switch が 2 か所以上にあります。値ごとの違いを 1 か所に持つ形にしてください");
    }

    [Fact]
    public void testsに同じ分類のcaseの並びが2か所以上にない()
    {
        AssertNoSimilarLabels(TestTexts(), false, NoAllowed, "同じ分類の case の並びが 2 か所以上にあります。分類を 1 つの補助にして、そこから使ってください");
    }

    [Fact]
    public void testsに同じ値の集合を分岐するswitchが2か所以上にない()
    {
        AssertNoSimilarLabels(TestTexts(), true, AllowedSameTestSwitches, "同じ値の集合を分岐する switch が 2 か所以上にあります。1 つの補助にしてください");
    }

    [Fact]
    public void caseの並びの検査は同じ集合とほとんど同じ集合を見つけ別の集合と少ない並びは除く()
    {
        const string first = "class A { bool F(K k) { switch (k) { case K.A: case K.B: case K.C: case K.D: return true; default: return false; } } }";
        const string same = "class B { bool G(K k) { switch (k) { case K.D: case K.C: case K.B: case K.A: return 1 > 0; default: return false; } } }";
        const string nearly = "class C { int H(K k) { switch (k) { case K.Z: case K.A: case K.B: case K.C: case K.D: return 1; default: return 0; } } }";
        const string other = "class D { bool I(K k) { switch (k) { case K.P: case K.Q: case K.R: case K.S: return true; default: return false; } } }";
        const string few = "class E { bool J(K k) { switch (k) { case K.A: case K.B: case K.C: return true; default: return false; } } }";
        const string expression = "class F { bool L(K k) => k switch { K.A or K.B or K.C or K.D => true, _ => false }; }";
        Assert.Equal(new[] { "A.F", "B.G" }, Members(first, same, false));
        Assert.Equal(new[] { "A.F", "C.H" }, Members(first, nearly, false));
        Assert.Equal(new[] { "A.F", "F.L" }, Members(first, expression, false));
        Assert.Empty(CloneDetector.SimilarLabelSets(new[] { ("a.cs", first), ("d.cs", other) }, MinimumLabels, false));
        Assert.Empty(CloneDetector.SimilarLabelSets(new[] { ("a.cs", first), ("e.cs", few) }, MinimumLabels, false));
        // 型名の有無は区別しない。
        const string bare = "class G { bool M(K k) { switch (k) { case A: case B: case C: case D: return true; default: return false; } } }";
        Assert.Equal(new[] { "A.F", "G.M" }, Members(first, bare, false));
    }

    [Fact]
    public void switch全体の検査は分岐ごとに1つずつ並べた同じ値の集合を見つけ分岐ごとの並びの検査には現れない()
    {
        const string first = "class A { int F(K k) { switch (k) { case K.A: return 1; case K.B: return 2; case K.C: return 3; case K.D: return 4; default: return 0; } } }";
        const string second = "class B { string G(K k) => k switch { K.A => \"a\", K.B => \"b\", K.C => \"c\", K.D => \"d\", _ => \"\" }; }";
        const string third = "class C { int H(K k) { switch (k) { case K.A: return 1; case K.B: return 2; default: return 0; } } }";
        Assert.Equal(new[] { "A.F", "B.G" }, Members(first, second, true));
        Assert.Empty(CloneDetector.SimilarLabelSets(new[] { ("a.cs", first), ("c.cs", third) }, MinimumLabels, true));
        Assert.Empty(CloneDetector.SimilarLabelSets(new[] { ("a.cs", first), ("b.cs", second) }, MinimumLabels, false));
    }

    private static string[] Members(string first, string second, bool wholeSwitch) =>
        CloneDetector.SimilarLabelSets(new[] { ("a.cs", first), ("b.cs", second) }, MinimumLabels, wholeSwitch).Single()
            .Select(set => set.Member).OrderBy(name => name, StringComparer.Ordinal).ToArray();

    // ---------------------------------------------------------------- 同じ名前と引数の static メソッド

    /// <summary>
    /// 別のファイルに同じ名前と引数の型で定義してよい src の static メソッドと、その理由(キーは「名前(引数の型, …)」)。
    /// 理由を書けないものは許さず、1 か所に置いて使う。名前だけ同じで目的が違うものは、目的が分かる名前に変える。
    /// </summary>
    private static readonly Dictionary<string, string> AllowedSameNameSrcHelpers = new()
    {
        ["Apply(IrDocument, IrDiagnostics)"] =
            "IR の変換(背景の除去・グループの平坦化・画層ごとのグループ化)の入口の名前をそろえた規約。型の名前(BackgroundRemover など)が目的を表し、"
            + "本体は別々の変換で、同じ処理の書き写しではない。",
    };

    /// <summary>別のファイルに同じ名前と引数の型で定義してよい試験の static メソッドと、その理由。</summary>
    private static readonly Dictionary<string, string> AllowedSameNameTestHelpers = new();

    private static void AssertNoSameNameHelpers((string Path, string Text)[] sources, Dictionary<string, string> allowed)
    {
        var groups = CloneDetector.SameNameHelpers(sources);
        var unexpected = groups.Where(group => !allowed.ContainsKey(group[0].Signature)).ToArray();
        Assert.True(unexpected.Length == 0,
            "同じ名前と引数の static メソッドが 2 つ以上のファイルにあります(" + unexpected.Length + " 組)。1 か所に置いて使うか、目的が違うなら目的が分かる名前にしてください:\n"
            + string.Join("\n", unexpected.Select(group => group[0].Signature + "  " + string.Join(" ", group.Select(helper => helper.Place)))));
        AssertAllowedStillFound(allowed.Keys, groups.Select(group => group[0].Signature));
    }

    [Fact]
    public void srcに同じ名前と引数のstaticメソッドが2つ以上のファイルにない()
    {
        AssertNoSameNameHelpers(SrcTexts(), AllowedSameNameSrcHelpers);
    }

    [Fact]
    public void testsに同じ名前と引数のstaticメソッドが2つ以上のファイルにない()
    {
        AssertNoSameNameHelpers(TestTexts(), AllowedSameNameTestHelpers);
    }

    [Fact]
    public void 同じ名前の補助の検査は引数の型が同じstaticメソッドを見つけインスタンスと引数が違うものと同じファイルと試験のメソッドは除く()
    {
        const string first = "class A { private static int Twice(int x) { return x * 2; } }";
        const string second = "class B { internal static int Twice(int y) => y + y; }";
        var found = CloneDetector.SameNameHelpers(new[] { ("a.cs", first), ("b.cs", second) });
        Assert.Equal(new[] { "a.cs:1", "b.cs:1" }, found.Single().Select(helper => helper.Place).ToArray());
        // 引数の型が違えば別の補助。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", first), ("c.cs", "class C { static int Twice(double x) => x * 2; }") }));
        // インスタンスのメソッド(同じインターフェイスを実装する偽物)は比べない。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", first), ("d.cs", "class D { public int Twice(int x) => x; }") }));
        // 同じファイルの中の同名(別の型)は 2 つのファイルにならない。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", first + "class E { static int Twice(int x) => x; }") }));
        // 自分の型を返す作成の関数は、型ごとに同じ名前で持つので比べない。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", "class R { static R Failed(string m) => new R(); }"), ("b.cs", "class S { internal static S Failed(string m) => new S(); }") }));
        // Win32 の関数の宣言は本体を持たない。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", "class H { [DllImport(\"gdi32.dll\")] static extern bool DeleteDC(IntPtr h); }"), ("b.cs", "class I { [DllImport(\"gdi32.dll\")] static extern bool DeleteDC(IntPtr h); }") }));
        // 試験のメソッドは補助ではない。
        Assert.Empty(CloneDetector.SameNameHelpers(new[] { ("a.cs", "class F { [Fact] public static void Check() { } }"), ("b.cs", "class G { [Fact] public static void Check() { } }") }));
    }

    // ---------------------------------------------------------------- UTF-8 の作成

    /// <summary>src で UTF-8(BOM なし)の <c>UTF8Encoding</c> を作る唯一の場所。</summary>
    private const string Utf8Owner = "CadPeta.UseCases/TextEncodings.cs";

    internal static string[] Utf8EncodingCreations(string text) =>
        ParseVariants(text).SelectMany(Creations)
            .Where(creation => creation.TypeName.EndsWith("UTF8Encoding", StringComparison.Ordinal))
            .Select(creation => Place(creation.Node))
            .Distinct().ToArray();

    [Fact]
    public void srcでUTF8Encodingを作るのはTextEncodingsの1か所だけにある()
    {
        var all = SrcTexts().SelectMany(source => Utf8EncodingCreations(source.Text).Select(line => source.Path + ":" + line)).ToArray();
        var outside = all.Where(found => !found.StartsWith(Utf8Owner + ":", StringComparison.Ordinal)).ToArray();
        Assert.True(outside.Length == 0, "UTF8Encoding を " + Utf8Owner + " の外で作っています(" + outside.Length + " か所)。TextEncodings.Utf8NoBom を使ってください:\n" + string.Join("\n", outside));
        Assert.True(all.Length == 1, "UTF8Encoding を作るのは " + Utf8Owner + " の 1 か所だけです(" + all.Length + " か所):\n" + string.Join("\n", all));
    }

    [Fact]
    public void UTF8Encodingの作成の検査は完全修飾名も見つけEncodingUTF8は除く()
    {
        Assert.Single(Utf8EncodingCreations("class A { object a = new UTF8Encoding(false); }"));
        Assert.Single(Utf8EncodingCreations("class A { object a = new System.Text.UTF8Encoding(true); }"));
        Assert.Empty(Utf8EncodingCreations("class A { object a = Encoding.UTF8; object b = new Other(); }"));
        // 型を省略した new は、宣言の型で判定する。
        Assert.Equal(new[] { 1, 1, 1, 0 }, new[]
        {
            "class A { UTF8Encoding a = new(false); }",
            "class A { void F() { System.Text.UTF8Encoding a = new(true); } }",
            "class A { UTF8Encoding P { get; } = new(false); }",
            "class A { Other a = new(false); }",
        }.Select(source => Utf8EncodingCreations(source).Length).ToArray());
    }

    // ---------------------------------------------------------------- mm とインチの換算

    /// <summary>1 インチの mm(25.4)を数値で書いてよい唯一のファイル。</summary>
    private const string InchOwner = "CadPeta.Core/Geometry/Units.cs";

    /// <summary>数値のリテラル 25.4 の場所。コメントと文字列の中の 25.4 は含めない。</summary>
    internal static string[] MillimetersPerInchLiterals(string text) =>
        ParseVariants(text).SelectMany(root => root.DescendantNodes()).OfType<LiteralExpressionSyntax>()
            .Where(literal => literal.IsKind(SyntaxKind.NumericLiteralExpression) && Convert.ToDouble(literal.Token.Value, System.Globalization.CultureInfo.InvariantCulture) == 25.4)
            .Select(literal => Place(literal))
            .Distinct().ToArray();

    [Fact]
    public void srcで1インチの25_4はUnitsにだけ書く()
    {
        var violations = SrcTexts().Where(source => source.Path != InchOwner)
            .SelectMany(source => MillimetersPerInchLiterals(source.Text).Select(line => source.Path + ":" + line)).ToArray();
        Assert.True(violations.Length == 0, "25.4 が " + InchOwner + " の外にあります(" + violations.Length + " か所)。Units.MmPerInch を使ってください:\n" + string.Join("\n", violations));
    }

    [Fact]
    public void インチの検査は数値の25_4を見つけ文字列とコメントと別の数値は除く()
    {
        Assert.Single(MillimetersPerInchLiterals("class A { double a = x / 25.4; }"));
        Assert.Single(MillimetersPerInchLiterals("class A { const double MmPerInch = 25.4; }"));
        Assert.Single(MillimetersPerInchLiterals("class A { double a = 25.40 * x; }"));
        Assert.Empty(MillimetersPerInchLiterals("class A { string a = \"25.4\"; double b = 25.5; double c = 254; } // 25.4"));
    }
}
