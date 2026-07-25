// AutoCAD.NET.Model 24.3.0（net48＝AutoCAD 2024 が読み込む参照アセンブリ）には
// LinetypeTableRecord.ShapeIsUprightAt が無い。さらに AutoCAD 2024 実機の acdbmgd.dll
// （24.3.222.0.0）を ildasm で全ダンプ検索しても当該メンバーは存在しない（2026-07-10 実測。
// 「参照アセンブリの記載漏れで実機には実在する」という当初の想定は実機検証で否定された）。
// つまり 2024 世代の .NET API には正立フラグ（線種記号の U= 指定）を読む手段がない。
//
// そのため net48 では反射でメソッドを探し、無ければ「未対応」を返す。呼び出し側は
// 正立指定を相対回転として近似し、その事実を診断ログへ記録する（左向きの区間では記号が
// 上下逆に描かれ得る近似。2025 以降=net8/net10 の API は正確に判定できる）。
// 反射解決の失敗を型初期化子で throw しない: throw すると TypeInitializationException が
// 記号展開の外へ伝播してコマンド全体を殺す（AutoCAD 2024 実機で実測した欠陥の修正）。
#if NET48
using System.Reflection;
#endif

namespace CadPeta.Acquire.Acad;

internal static class LinetypeShapeUprightCompat
{
#if NET48
    static readonly MethodInfo? Method =
        typeof(Autodesk.AutoCAD.DatabaseServices.LinetypeTableRecord)
            .GetMethod("ShapeIsUprightAt", new[] { typeof(int) });

    /// <summary>実行中のホストの .NET API で正立フラグを読めるか。</summary>
    public static bool IsSupported => Method != null;

    /// <summary>正立フラグを返す。未対応のホスト（AutoCAD 2024 世代）では常に false
    /// （=相対回転として近似。呼び出し側が診断ログへ記録する）。</summary>
    public static bool ShapeIsUprightAt(Autodesk.AutoCAD.DatabaseServices.LinetypeTableRecord ltr, int index) =>
        Method != null && (bool)Method.Invoke(ltr, new object[] { index })!;
#else
    public static bool IsSupported => true;

    public static bool ShapeIsUprightAt(Autodesk.AutoCAD.DatabaseServices.LinetypeTableRecord ltr, int index) =>
        ltr.ShapeIsUprightAt(index);
#endif
}
