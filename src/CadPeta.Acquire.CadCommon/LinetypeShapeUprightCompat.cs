// 線種記号の正立フラグ（U= 指定）を読む LinetypeTableRecord.ShapeIsUprightAt は、次の .NET API に無い
// （実測。4 製品で共有し、違いは #if で分ける）。
//   - AutoCAD 2024（net48）: AutoCAD.NET.Model 24.3.0 の参照アセンブリにも、AutoCAD 2024 実機の acdbmgd.dll
//     （24.3.222.0.0）を ildasm で全ダンプ検索しても、当該メンバーは存在しない（2026-07-10 実測。
//     「参照アセンブリの記載漏れで実機には実在する」という当初の想定は実機検証で否定された）。
//     2025 以降（net8/net10）の AutoCAD の API は正確に判定できる。
//   - ARES Standard 2024 = TD_Mgd_23.1_16.dll（2026-07-10）、ARES Standard 2027 = TD_Mgd_Net8_26.8_17.dll
//     （2026-07-13）。ODA 26.8 系は同義のフラグを ShapeIsUcsOrientedAt という別名で公開するが、正立（U=）と
//     UCS 整列の意味が一致する保証は本機のヘッドレス環境では実行時検証できない〔ARES は GUI 必須〕。
//     未検証の別名 API へ差し替えて誤描画を招くより、2024 で検証済みの近似経路へ揃える方が安全。
//     ShapeIsUcsOrientedAt の採用は GUI 同席での意味検証を前提に将来判断する。
//   - IJCAD 2026 PRO = GcDbMgd.dll、BricsCAD V26 = TD_Mgd.dll（2026-07-11 リフレクション実機検査）。
//
// そのため AutoCAD の net8/net10 以外では反射でメソッドを探し、無ければ「未対応」を返す。呼び出し側は
// 正立指定を相対回転として近似し、その事実を診断ログへ記録する（左向きの区間では記号が上下逆に
// 描かれ得る近似）。
// 反射解決の失敗を型初期化子で throw しない: throw すると TypeInitializationException が
// 記号展開の外へ伝播してコマンド全体を殺す（AutoCAD 2024 実機で実測した欠陥の修正）。
using System;
using System.Reflection;

#if CADPETA_ACAD
using LinetypeTableRecord = Autodesk.AutoCAD.DatabaseServices.LinetypeTableRecord;
namespace CadPeta.Acquire.Acad;
#elif CADPETA_ARES
using LinetypeTableRecord = Teigha.DatabaseServices.LinetypeTableRecord;
namespace CadPeta.Acquire.Ares;
#elif CADPETA_IJCAD
using LinetypeTableRecord = Itjp.Ijcad.DatabaseServices.LinetypeTableRecord;
namespace CadPeta.Acquire.Ijcad;
#elif CADPETA_BRICSCAD
// BricsCAD の DB 系 API は TD_Mgd.dll のまま Teigha.* 名前空間を使う（App/Editor 系だけ Bricscad.* へ差し替わる）
using LinetypeTableRecord = Teigha.DatabaseServices.LinetypeTableRecord;
namespace CadPeta.Acquire.Bricscad;
#else
#error CAD product symbol is required
#endif

internal static class LinetypeShapeUprightCompat
{
#if CADPETA_ACAD && !NET48
    public static bool IsSupported => true;

    public static bool ShapeIsUprightAt(LinetypeTableRecord ltr, int index) =>
        ltr.ShapeIsUprightAt(index);
#else
    static readonly MethodInfo? Method =
        typeof(LinetypeTableRecord).GetMethod("ShapeIsUprightAt", new[] { typeof(int) });

    /// <summary>実行中のホストの .NET API で正立フラグを読めるか。</summary>
    public static bool IsSupported => Method != null;

    /// <summary>正立フラグを返す。未対応のホスト（上の一覧）では常に false
    /// （=相対回転として近似。呼び出し側が診断ログへ記録する）。</summary>
    public static bool ShapeIsUprightAt(LinetypeTableRecord ltr, int index) =>
        Method != null && (bool)Method.Invoke(ltr, new object[] { index })!;
#endif
}
