// ARES が読み込む参照アセンブリには Teigha.DatabaseServices.LinetypeTableRecord.ShapeIsUprightAt が
// どの世代にも無い。実測（リフレクション実機検査）:
//   - ARES Standard 2024 = TD_Mgd_23.1_16.dll に ShapeIsUprightAt 無し（2026-07-10。AutoCAD 2024
//     世代の .NET API と同じ欠落）。
//   - ARES Standard 2027 = TD_Mgd_Net8_26.8_17.dll にも ShapeIsUprightAt 無し（2026-07-13。ODA 26.8
//     系は同義のフラグを ShapeIsUcsOrientedAt という別名で公開するが、正立（U=）と UCS 整列の
//     意味が一致する保証は本機のヘッドレス環境では実行時検証できない〔ARES は GUI 必須〕。
//     未検証の別名 API へ差し替えて誤描画を招くより、2024 で検証済みの近似経路へ揃える方が安全。
//     ShapeIsUcsOrientedAt の採用は GUI 同席での意味検証を前提に将来判断する）。
// つまり ARES のどの所有世代でも、この名前の API では正立フラグ（線種記号の U= 指定）を読めない。
//
// そのため net48・net8 の両ターゲットで反射により ShapeIsUprightAt を探し、無ければ「未対応」を
// 返す。呼び出し側は正立指定を相対回転として近似し、その事実を診断ログへ記録する（左向きの区間
// では記号が上下逆に描かれ得る近似）。ARES は所有2世代とも常に「未対応」へ落ちるため、AutoCAD
// 2024・IJCAD・BricsCAD と同じ実測済みの近似挙動に一致する。
// 反射解決の失敗を型初期化子で throw しない: throw すると TypeInitializationException が
// 記号展開の外へ伝播してコマンド全体を殺す（AutoCAD 版アダプタで実測した欠陥と同じ扱い）。
using System.Reflection;

namespace CadPeta.Acquire.Ares;

internal static class LinetypeShapeUprightCompat
{
    static readonly MethodInfo? Method =
        typeof(Teigha.DatabaseServices.LinetypeTableRecord)
            .GetMethod("ShapeIsUprightAt", new[] { typeof(int) });

    /// <summary>実行中のホストの .NET API で正立フラグを読めるか。ARES は所有2世代（2024/2027）とも
    /// この名前の API を持たないため常に false。</summary>
    public static bool IsSupported => Method != null;

    /// <summary>正立フラグを返す。未対応のホスト（ARES 2024/2027 世代）では常に false
    /// （=相対回転として近似。呼び出し側が診断ログへ記録する）。</summary>
    public static bool ShapeIsUprightAt(Teigha.DatabaseServices.LinetypeTableRecord ltr, int index) =>
        Method != null && (bool)Method.Invoke(ltr, new object[] { index })!;
}
