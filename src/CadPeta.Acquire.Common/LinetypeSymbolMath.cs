// 複雑線種の記号（シェイプ・文字）展開の純粋計算部分。特定のCAD製品APIに依存しない
// 共有計算のため、取得アダプタ間で共有し単体テスト対象にする。
// 角度・オフセットの意味論は本機実物（acadiso.lin の GAS_LINE 等）と自作の R=/A=/U= 実験線種を
// entget で実測して確定した:
// - group74 の各ビットは C# の LinetypeTableRecord.ShapeIsUcsOrientedAt（A=＝絶対回転）・
//   ShapeIsUprightAt（U=＝正立）に対応する。両方 false なら R=（既定・相対回転）
// - R= はビットを立てない。ShapeRotationAt の値は常に生の指定角（ラジアン）で、
//   相対/絶対/正立のどれで解釈するかを上記2フラグが決める
// - 群44=X（接線方向オフセット）・群45=Y（左法線方向オフセット）・群46=尺度・群50=回転角
namespace CadPeta.Acquire.Common;

public static class LinetypeSymbolMath
{
    /// <summary>
    /// 記号の最終回転角（ラジアン、CAD の反時計回り正・Y上向き系）を解決する。
    /// - ucsOriented（A=）: 絶対回転。曲線の接線方向に関わらず rotationRad をそのまま使う
    /// - upright（U=）: 相対回転（接線角+rotationRad）を求めた上で、その向きが「逆向き
    ///   （左向き寄り、cos&lt;0）」なら180°回して正立を保つ
    /// - どちらでもない（R=既定）: 接線角+rotationRad
    /// </summary>
    public static double ResolveAngle(double tangentAngleRad, double rotationRad,
                                      bool ucsOriented, bool upright)
    {
        if (ucsOriented)
            return rotationRad;
        double rel = tangentAngleRad + rotationRad;
        if (upright && Math.Cos(rel) < 0)
            rel += Math.PI;
        return rel;
    }

    /// <summary>
    /// 破線パターンの長さ0要素を、視認用の最小長ドットへ置換すべきか。
    /// 記号（シェイプ・文字）を運ぶ長さ0要素はドットではなく「その場に記号を置く」ための
    /// ペンアップ区間のため、最小長へ置換すると実在しない点が描かれる（偽ドット）。
    /// 記号を持たない長さ0要素だけを置換対象とする。
    /// </summary>
    public static bool ShouldSubstituteDotLength(double dashLengthDu, bool hasSymbol) =>
        dashLengthDu == 0 && !hasSymbol;
}
