// IJCAD 2026 PRO が読み込む参照アセンブリ GcDbMgd.dll には
// Itjp.Ijcad.DatabaseServices.LinetypeTableRecord.ShapeIsUprightAt が無い（2026-07-11
// リフレクション実機検査。ARES 2024 世代・AutoCAD 2024 世代の .NET API と同じ欠落）。
// つまり IJCAD の .NET API にも正立フラグ（線種記号の U= 指定）を読む手段がない。
//
// ARES 版（net48 単一ターゲット）は #if NET48 で反射経路を切り替えていたが、IJCAD は net8
// ターゲットでも API が無いため、TFM で分岐せず常に反射経路を使う（本ファイルの唯一の
// ロジック差）。呼び出し側は正立指定を相対回転として近似し、その事実を診断ログへ記録する
// （左向きの区間では記号が上下逆に描かれ得る近似）。
// 反射解決の失敗を型初期化子で throw しない: throw すると TypeInitializationException が
// 記号展開の外へ伝播してコマンド全体を殺す（AutoCAD 版アダプタで実測した欠陥と同じ扱い）。
using System.Reflection;

namespace CadPeta.Acquire.Ijcad;

internal static class LinetypeShapeUprightCompat
{
    static readonly MethodInfo? Method =
        typeof(Itjp.Ijcad.DatabaseServices.LinetypeTableRecord)
            .GetMethod("ShapeIsUprightAt", new[] { typeof(int) });

    /// <summary>実行中のホストの .NET API で正立フラグを読めるか。</summary>
    public static bool IsSupported => Method != null;

    /// <summary>正立フラグを返す。未対応のホスト（IJCAD 2026）では常に false
    /// （=相対回転として近似。呼び出し側が診断ログへ記録する）。</summary>
    public static bool ShapeIsUprightAt(Itjp.Ijcad.DatabaseServices.LinetypeTableRecord ltr, int index) =>
        Method != null && (bool)Method.Invoke(ltr, new object[] { index })!;
}
