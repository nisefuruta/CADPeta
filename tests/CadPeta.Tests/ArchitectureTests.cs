// 参照方向の機械検査（内核は外部参照ゼロ、アダプタは内側へ一方向のみという設計方針を検査する）。
// csproj のコメント規約を、ビルド成果物のアセンブリ参照で裏取りする。
using System.Reflection;
using Xunit;

namespace CadPeta.Tests;

public class ArchitectureTests
{
    static string[] RefNames(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name ?? "").ToArray();

    static bool IsBcl(string name) =>
        name == "System" || name.StartsWith("System.") || name == "netstandard" || name == "mscorlib";

    [Fact]
    public void 内核は外部アセンブリを参照しない()
    {
        var refs = RefNames(typeof(Ir.IrDocument).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r), $"内核が BCL 以外を参照: {r}"));
    }

    [Fact]
    public void ユースケース層は内核とBCLのみ参照する()
    {
        var refs = RefNames(typeof(UseCases.ConvertPipeline).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core",
            $"ユースケース層が内核以外を参照: {r}"));
    }

    [Fact]
    public void 取得アダプタと出力アダプタは互いを参照しない()
    {
        var acquirePdfRefs = RefNames(typeof(Acquire.Pdf.PdfIrAcquirer).Assembly);
        var acquireAcadRefs = RefNames(typeof(Acquire.Acad.CadIrAcquirer).Assembly);
        var emfRefs = RefNames(typeof(Out.Emf.EmfWriter).Assembly);
        var pdfOutRefs = RefNames(typeof(Out.Pdf.PdfWriter).Assembly);
        var pngOutRefs = RefNames(typeof(Out.Png.PngWriter).Assembly);
        var acquireAssemblyNames = new[] { "CadPeta.Acquire.Pdf", "CadPeta.Acquire.Acad" };
        var outAssemblyNames = new[] { "CadPeta.Out.Emf", "CadPeta.Out.Pdf", "CadPeta.Out.Png" };

        // 取得アダプタは出力アダプタを参照しない
        foreach (var refs in new[] { acquirePdfRefs, acquireAcadRefs })
            foreach (var outName in outAssemblyNames)
                Assert.DoesNotContain(outName, refs);
        // 出力アダプタは取得アダプタを参照しない
        foreach (var refs in new[] { emfRefs, pdfOutRefs, pngOutRefs })
            foreach (var acqName in acquireAssemblyNames)
                Assert.DoesNotContain(acqName, refs);
        // 取得アダプタ同士・出力アダプタ同士も互いに参照しない（各アダプタは境界ポート越しにのみ結線される）
        Assert.DoesNotContain("CadPeta.Acquire.Acad", acquirePdfRefs);
        Assert.DoesNotContain("CadPeta.Acquire.Pdf", acquireAcadRefs);
        Assert.DoesNotContain("CadPeta.Out.Pdf", emfRefs);
        Assert.DoesNotContain("CadPeta.Out.Png", emfRefs);
        Assert.DoesNotContain("CadPeta.Out.Emf", pdfOutRefs);
        Assert.DoesNotContain("CadPeta.Out.Png", pdfOutRefs);
        Assert.DoesNotContain("CadPeta.Out.Emf", pngOutRefs);
        Assert.DoesNotContain("CadPeta.Out.Pdf", pngOutRefs);
    }

    [Fact]
    public void PDF出力はBCLとCore及びUseCasesだけを参照する()
    {
        // PDF出力（自前の最小PDFライタ）はパッケージ参照ゼロ・外部プロジェクト参照は
        // 内核2つ（Core・UseCases）だけを許す（CadPeta.Out.Pdf.csproj のコメント規約の裏取り）。
        var refs = RefNames(typeof(Out.Pdf.PdfWriter).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core" || r == "CadPeta.UseCases",
            $"CadPeta.Out.Pdf が想定外のアセンブリを参照: {r}"));
    }

    [Fact]
    public void PDF出力はPInvokeを持たない()
    {
        // 自前の最小PDFライタのみで完結させる方針（GDI等のWin32依存はEMF/PNG出力に閉じる）の検査。
        var asm = typeof(Out.Pdf.PdfWriter).Assembly;
        foreach (var type in asm.GetTypes())
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.DeclaredOnly))
            Assert.False((m.Attributes & MethodAttributes.PinvokeImpl) != 0,
                $"CadPeta.Out.Pdf に P/Invoke がある: {type.FullName}.{m.Name}");
    }

    /// <summary>AutoCAD 参照アセンブリ名（NuGet AutoCAD.NET.Core / .Model の実体）。</summary>
    static readonly string[] AutocadAssemblyNames = { "accoremgd", "acdbmgd", "acmgd" };

    static bool IsAutocad(string name) =>
        AutocadAssemblyNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void AutoCAD参照はプラグイン合成ルートと構造SVG書き出しアダプタだけに置く()
    {
        // 内核・ユースケース層・PDF印刷経路/EMF/PDF/PNG アダプタ・SHX解釈器・取得アダプタ共有計算層に
        // AutoCAD の型が現れてはならない
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
        };
        foreach (var asm in inner)
            Assert.All(RefNames(asm), r => Assert.False(IsAutocad(r),
                $"{asm.GetName().Name} が AutoCAD アセンブリを参照: {r}"));

        // プラグインは合成ルートとして内側ポート（ユースケース層）越しに結線する
        var pluginRefs = RefNames(typeof(Plugin.Acad.PetaCommand).Assembly);
        Assert.Contains("CadPeta.UseCases", pluginRefs);
        Assert.Contains(pluginRefs, IsAutocad);

        // 構造SVG書き出しの取得アダプタは AutoCAD API を内核 IR へ写像する層で、AutoCAD 参照を持つ
        var acadRefs = RefNames(typeof(Acquire.Acad.CadIrAcquirer).Assembly);
        Assert.Contains("CadPeta.Core", acadRefs);
        Assert.Contains(acadRefs, IsAutocad);
    }

    /// <summary>ARES 参照アセンブリ名（HintPath 参照の実体。互換CAD第一対象）。</summary>
    static readonly string[] AresAssemblyNames = { "TD_Mgd_23.1_16", "FxCoreMgd_23.1_16" };

    static bool IsAres(string name) =>
        AresAssemblyNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void ARES参照はプラグイン合成ルートと構造SVG書き出しアダプタだけに置く()
    {
        // 内核・ユースケース層・PDF印刷経路/EMF/PDF/PNG アダプタ・SHX解釈器・取得アダプタ共有計算層・
        // AutoCAD版の合成ルートと構造SVG書き出しアダプタに ARES の型が現れてはならない
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
            Assert.All(RefNames(asm), r => Assert.False(IsAres(r),
                $"{asm.GetName().Name} が ARES アセンブリを参照: {r}"));

        // プラグインは合成ルートとして内側ポート（ユースケース層）越しに結線する
        var pluginRefs = RefNames(typeof(Plugin.Ares.PetaCommand).Assembly);
        Assert.Contains("CadPeta.UseCases", pluginRefs);
        Assert.Contains(pluginRefs, IsAres);

        // 構造SVG書き出し（互換CAD第一対象）取得アダプタは ARES API を内核 IR へ写像する層で、ARES 参照を持つ
        var aresRefs = RefNames(typeof(Acquire.Ares.CadIrAcquirer).Assembly);
        Assert.Contains("CadPeta.Core", aresRefs);
        Assert.Contains(aresRefs, IsAres);
    }

    [Fact]
    public void Aresプロジェクトは内核と共有計算層と他アダプタから参照されない()
    {
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
        {
            Assert.DoesNotContain("CadPeta.Acquire.Ares", RefNames(asm));
            Assert.DoesNotContain("CadPeta.Plugin.Ares", RefNames(asm));
        }
    }

    // CadPeta.Acquire.Ijcad / CadPeta.Plugin.Ijcad は net8 単一ターゲット。net48 コンシューマは
    // NuGet 上 net8 専用プロジェクトを参照できない（ARES=net48 単一とは逆方向の非対称性）ため、
    // net48 ビルドではテスト側の ProjectReference 自体を外している
    // （CadPeta.Tests.csproj 参照）。本ブロックはその条件と対応させる。
#if !NET48
    /// <summary>IJCAD 参照アセンブリ名（HintPath 参照の実体。互換CAD第二対象）。</summary>
    static readonly string[] IjcadAssemblyNames = { "GcDbMgd", "GcCoreMgd", "GcMgd" };

    static bool IsIjcad(string name) =>
        IjcadAssemblyNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void IJCAD参照はプラグイン合成ルートと構造SVG書き出しアダプタだけに置く()
    {
        // 内核・ユースケース層・PDF印刷経路/EMF/PDF/PNG アダプタ・SHX解釈器・取得アダプタ共有計算層・
        // AutoCAD版/ARES版/BricsCAD版の合成ルートと構造SVG書き出しアダプタに IJCAD の型が現れてはならない
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
            typeof(Acquire.Ares.CadIrAcquirer).Assembly,
            typeof(Plugin.Ares.PetaCommand).Assembly,
            typeof(Acquire.Bricscad.CadIrAcquirer).Assembly,
            typeof(Plugin.Bricscad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
            Assert.All(RefNames(asm), r => Assert.False(IsIjcad(r),
                $"{asm.GetName().Name} が IJCAD アセンブリを参照: {r}"));

        // プラグインは合成ルートとして内側ポート（ユースケース層）越しに結線する
        var pluginRefs = RefNames(typeof(Plugin.Ijcad.PetaCommand).Assembly);
        Assert.Contains("CadPeta.UseCases", pluginRefs);
        Assert.Contains(pluginRefs, IsIjcad);

        // 構造SVG書き出し（互換CAD第二対象）取得アダプタは IJCAD API を内核 IR へ写像する層で、IJCAD 参照を持つ
        var ijcadRefs = RefNames(typeof(Acquire.Ijcad.CadIrAcquirer).Assembly);
        Assert.Contains("CadPeta.Core", ijcadRefs);
        Assert.Contains(ijcadRefs, IsIjcad);
    }

    [Fact]
    public void Ijcadプロジェクトは内核と共有計算層と他アダプタから参照されない()
    {
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
            typeof(Acquire.Ares.CadIrAcquirer).Assembly,
            typeof(Plugin.Ares.PetaCommand).Assembly,
            typeof(Acquire.Bricscad.CadIrAcquirer).Assembly,
            typeof(Plugin.Bricscad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
        {
            Assert.DoesNotContain("CadPeta.Acquire.Ijcad", RefNames(asm));
            Assert.DoesNotContain("CadPeta.Plugin.Ijcad", RefNames(asm));
        }
    }

    /// <summary>BricsCAD 参照アセンブリ名（HintPath 参照の実体。互換CAD第三対象）。</summary>
    static readonly string[] BricscadAssemblyNames = { "TD_Mgd", "BrxMgd" };

    static bool IsBricscad(string name) =>
        BricscadAssemblyNames.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void BricsCAD参照はプラグイン合成ルートと構造SVG書き出しアダプタだけに置く()
    {
        // 内核・ユースケース層・PDF印刷経路/EMF/PDF/PNG アダプタ・SHX解釈器・取得アダプタ共有計算層・
        // AutoCAD版/ARES版/IJCAD版の合成ルートと構造SVG書き出しアダプタに BricsCAD の型が現れてはならない。
        // BricsCAD の TD_Mgd（"TD_Mgd_23.1_16" とは完全一致比較のため衝突しない。IsAres 参照）は
        // ARES の DB 系と同じ Teigha.* 名前空間だが実体アセンブリ名は異なる
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
            typeof(Acquire.Ares.CadIrAcquirer).Assembly,
            typeof(Plugin.Ares.PetaCommand).Assembly,
            typeof(Acquire.Ijcad.CadIrAcquirer).Assembly,
            typeof(Plugin.Ijcad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
            Assert.All(RefNames(asm), r => Assert.False(IsBricscad(r),
                $"{asm.GetName().Name} が BricsCAD アセンブリを参照: {r}"));

        // プラグインは合成ルートとして内側ポート（ユースケース層）越しに結線する
        var pluginRefs = RefNames(typeof(Plugin.Bricscad.PetaCommand).Assembly);
        Assert.Contains("CadPeta.UseCases", pluginRefs);
        Assert.Contains(pluginRefs, IsBricscad);

        // 構造SVG書き出し（互換CAD第三対象）取得アダプタは BricsCAD API を内核 IR へ写像する層で、
        // BricsCAD 参照を持つ
        var bricscadRefs = RefNames(typeof(Acquire.Bricscad.CadIrAcquirer).Assembly);
        Assert.Contains("CadPeta.Core", bricscadRefs);
        Assert.Contains(bricscadRefs, IsBricscad);
    }

    [Fact]
    public void Bricscadプロジェクトは内核と共有計算層と他アダプタから参照されない()
    {
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
            typeof(Plugin.Acad.PetaCommand).Assembly,
            typeof(Acquire.Ares.CadIrAcquirer).Assembly,
            typeof(Plugin.Ares.PetaCommand).Assembly,
            typeof(Acquire.Ijcad.CadIrAcquirer).Assembly,
            typeof(Plugin.Ijcad.PetaCommand).Assembly,
        };
        foreach (var asm in inner)
        {
            Assert.DoesNotContain("CadPeta.Acquire.Bricscad", RefNames(asm));
            Assert.DoesNotContain("CadPeta.Plugin.Bricscad", RefNames(asm));
        }
    }
#endif

    [Fact]
    public void 内核とユースケース層はPInvokeを持たない()
    {
        // Win32 依存（GDI・クリップボード等の DllImport）はアダプタ層に閉じる。
        // P/Invoke はアセンブリ参照に現れないため、参照検査とは別にメソッド属性で検査する
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
        };
        foreach (var asm in inner)
        foreach (var type in asm.GetTypes())
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.DeclaredOnly))
            Assert.False((m.Attributes & MethodAttributes.PinvokeImpl) != 0,
                $"{asm.GetName().Name} に P/Invoke がある: {type.FullName}.{m.Name}");
    }

    [Fact]
    public void プラグイン合成ルートと共有層は内側から参照されない()
    {
        // 内核・アダプタ・共有計算層は、プラグインの合成ルート（製品別の薄い殻）にも
        // プラグイン層の単一情報源（Plugin.Shared＝取得アダプタより上の合成層）にも依存しない
        var inner = new[]
        {
            typeof(Ir.IrDocument).Assembly,
            typeof(UseCases.ConvertPipeline).Assembly,
            typeof(Acquire.Pdf.PdfIrAcquirer).Assembly,
            typeof(Acquire.Acad.CadIrAcquirer).Assembly,
            typeof(Out.Emf.EmfWriter).Assembly,
            typeof(Out.Pdf.PdfWriter).Assembly,
            typeof(Out.Png.PngWriter).Assembly,
            typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly,
            typeof(Acquire.Common.CurveApprox).Assembly,
        };
        foreach (var asm in inner)
        {
            Assert.DoesNotContain("CadPeta.Plugin.Acad", RefNames(asm));
            Assert.DoesNotContain("CadPeta.Plugin.Shared", RefNames(asm));
        }
    }

    [Fact]
    public void 共有プラグイン層はBCLと内核だけを参照しCAD参照を持たない()
    {
        // プラグイン層の単一情報源（PetaRunner・RangePrompt・PlotOrchestrator・ports）は
        // 中立型だけで会話する。外部プロジェクト参照は内核2つ（Core・UseCases）と、
        // CAD 非依存の取得共通既定値と診断カテゴリ定数（AcquireDiagCategories）を置く
        // Acquire.Common だけを許し、
        // どの製品 CAD SDK の型も現れてはならない（CAD 固有処理は各殻の port 実装に封じ込める）。
        var refs = RefNames(typeof(Plugin.Shared.PetaRunner).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core" || r == "CadPeta.UseCases"
                || r == "CadPeta.Acquire.Common",
            $"CadPeta.Plugin.Shared が想定外のアセンブリを参照: {r}"));
        Assert.DoesNotContain(refs, IsAutocad);
        Assert.DoesNotContain(refs, IsAres);
        // IsIjcad / IsBricscad は net48 ビルドでは定義されない（当該2製品は net8 単一ターゲットで
        // net48 コンシューマから参照されないため #if !NET48 でガード）
#if !NET48
        Assert.DoesNotContain(refs, IsIjcad);
        Assert.DoesNotContain(refs, IsBricscad);
#endif
        // プラグインの薄い殻は共有層越しに本体フローへ結線する（合成ルートは Shared を参照する）
        Assert.Contains("CadPeta.Plugin.Shared", RefNames(typeof(Plugin.Acad.PetaCommand).Assembly));
    }

    [Fact]
    public void 共有プラグイン層はPInvokeを持たない()
    {
        // Win32 依存（GDI・クリップボード等）は殻・アダプタ層に閉じ、共有層は純粋に保つ。
        var asm = typeof(Plugin.Shared.PetaRunner).Assembly;
        foreach (var type in asm.GetTypes())
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.DeclaredOnly))
            Assert.False((m.Attributes & MethodAttributes.PinvokeImpl) != 0,
                $"CadPeta.Plugin.Shared に P/Invoke がある: {type.FullName}.{m.Name}");
    }

    [Fact]
    public void SHX解釈器はBCLとCore及びUseCasesだけを参照する()
    {
        // グリフ輪郭ポートの SHX 実装（取得系・出力系のどちらからも使われ得る独立プロジェクト）は
        // パッケージ参照ゼロ・外部プロジェクト参照は内核2つ（Core・UseCases）だけを許す。
        var refs = RefNames(typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core" || r == "CadPeta.UseCases",
            $"CadPeta.Glyphs.Shx が想定外のアセンブリを参照: {r}"));
    }

    [Fact]
    public void SHX解釈器はPInvokeを持たない()
    {
        // 自前のバイナリ解釈器のみで完結させる方針（Win32 依存はアダプタ層に閉じる）の検査。
        var asm = typeof(Glyphs.Shx.ShxGlyphOutliner).Assembly;
        foreach (var type in asm.GetTypes())
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                          BindingFlags.Static | BindingFlags.Instance |
                                          BindingFlags.DeclaredOnly))
            Assert.False((m.Attributes & MethodAttributes.PinvokeImpl) != 0,
                $"CadPeta.Glyphs.Shx に P/Invoke がある: {type.FullName}.{m.Name}");
    }

    [Fact]
    public void 取得アダプタ共有計算層はBCLとCoreだけを参照する()
    {
        // 特定のCAD製品APIに依存しない純粋計算（曲線近似・線種の端部整列・線種記号の角度解決・
        // 既定値・GDIフォントメトリクス）を構造SVG書き出しアダプタと互換CADアダプタの間で共有する層。
        // パッケージ参照ゼロ・外部プロジェクト参照は内核1つ（Core）だけを許す
        // （CadPeta.Acquire.Common.csproj のコメント規約の裏取り）。GDIフォントメトリクスの
        // Win32 P/Invoke はアダプタ支援として正当なため、P/Invoke ゼロの検査対象には含めない。
        var refs = RefNames(typeof(Acquire.Common.CurveApprox).Assembly);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core",
            $"CadPeta.Acquire.Common が想定外のアセンブリを参照: {r}"));
    }

    [Fact]
    public void グラデーションラスタ層はBCLとCoreとAcquireCommonだけを参照しCAD型を持たない()
    {
        // GradientEmitter が診断カテゴリ定数（AcquireDiagCategories）・取得共通既定値
        // （AcquireDefaults）を使うため Acquire.Common を参照する（両者ともCAD SDK非依存）。
        var asm = typeof(Acquire.Raster.GradientRasterizer).Assembly;
        var refs = RefNames(asm);
        Assert.All(refs, r => Assert.True(IsBcl(r) || r == "CadPeta.Core" || r == "CadPeta.Acquire.Common",
            $"CadPeta.Acquire.Raster が想定外のアセンブリを参照: {r}"));
        Assert.DoesNotContain(refs, IsAutocad);
        Assert.DoesNotContain(refs, IsAres);
#if !NET48
        Assert.DoesNotContain(refs, IsIjcad);
        Assert.DoesNotContain(refs, IsBricscad);
#endif
    }
}
