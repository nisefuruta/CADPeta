// BricsCAD V26 が読み込む参照アセンブリ TD_Mgd.dll には
// Teigha.DatabaseServices.LinetypeTableRecord.ShapeIsUprightAt が無い（2026-07-11
// リフレクション実機検査。ARES 2024 世代・AutoCAD 2024 世代・IJCAD 2026 世代の .NET API と
// 同じ欠落）。つまり BricsCAD V26 の .NET API にも正立フラグ（線種記号の U= 指定）を
// 読む手段がない。
//
// ARES 版（net48 単一ターゲット）は #if NET48 で反射経路を切り替えていたが、BricsCAD は
// IJCAD と同じく net8 ターゲットでも API が無いため、TFM で分岐せず常に反射経路を使う
// （本ファイルの唯一のロジック差）。DB 型は ARES と同じ Teigha.DatabaseServices（BricsCAD の
// DB 系 API は TD_Mgd.dll のまま Teigha.* 名前空間を使う。App/Editor 系だけ Bricscad.* へ
// 差し替わる非対称性が本アダプタ全体の唯一の設計上の要点）。呼び出し側は正立指定を
// 相対回転として近似し、その事実を診断ログへ記録する（左向きの区間では記号が上下逆に
// 描かれ得る近似）。
// 反射解決の失敗を型初期化子で throw しない: throw すると TypeInitializationException が
// 記号展開の外へ伝播してコマンド全体を殺す（AutoCAD 版アダプタで実測した欠陥と同じ扱い）。
using System.Reflection;

namespace CadPeta.Acquire.Bricscad;

internal static class LinetypeShapeUprightCompat
{
    static readonly MethodInfo? Method =
        typeof(Teigha.DatabaseServices.LinetypeTableRecord)
            .GetMethod("ShapeIsUprightAt", new[] { typeof(int) });

    /// <summary>実行中のホストの .NET API で正立フラグを読めるか。</summary>
    public static bool IsSupported => Method != null;

    /// <summary>正立フラグを返す。未対応のホスト（BricsCAD V26）では常に false
    /// （=相対回転として近似。呼び出し側が診断ログへ記録する）。</summary>
    public static bool ShapeIsUprightAt(Teigha.DatabaseServices.LinetypeTableRecord ltr, int index) =>
        Method != null && (bool)Method.Invoke(ltr, new object[] { index })!;
}
