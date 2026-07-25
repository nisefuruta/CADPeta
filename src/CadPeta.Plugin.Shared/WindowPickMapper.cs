// 窓 pick の座標契約（4製品で唯一の実装）。
//
// 規則: プロット窓（PlotType.Window で SetPlotWindowArea へ渡す矩形）は「ビューの DCS」で指定する。
// pick 点は現在 UCS で返るため、UCS→WCS→DCS へ写してから min/max を取る。
//
// 根拠（AutoCAD 2026 実機で確定。再現は tests/tools/regression/window_contract/）:
// - CAD 自身の -PLOT に窓の2点（現在 UCS）を与えると、ページ設定へ格納される窓
//   （DXF 群 48/49/140/141）は、その2点を DCS へ写した値の min/max と厳密に一致した。
//   対照・TARGET単独・UCS原点移動・UCS回転・ビューのねじれ・傾斜UCS・3Dビューの全ケースで成立する。
// - 同一図面で窓の値だけを変えて発行した比較では、DCS へ写した値だけが、TARGET=0 の対照と
//   画素まで完全一致するページを出した（生値・UCS→WCS はいずれも別領域になった）。
//
// この規則が「利用者が見ている範囲」と一致する理由: pick 点は画面クリックを UCS 平面へ投影した
// 点であり、投影は視線方向に沿うため画面上の位置は保たれる。DCS へ戻すとクリック位置そのものに
// 戻るので、DCS で min/max を取った矩形は利用者が画面でドラッグした矩形になる。UCS がどの軸へ
// 回っていても、ビューがねじれていても、3Dビューでも同じ理屈が成り立つ。
//
// 唯一成り立たないのは UCS 平面が視線と平行（真横から見る）な場合で、投影が定義できず窓が線に
// 潰れる。CAD 自身も窓プロットを中止するため、ここでも誤った成果物を出さずに報告する。
//
// CAD SDK 型を持ち込まない（各ポートが SDK 値を中立型へ移して渡す）。単体テスト対象。
using CadPeta.Geometry;

namespace CadPeta.Plugin.Shared;

/// <summary>中立の3次元ベクトル（CAD SDK 型を Shared へ持ち込まないための最小限の器）。</summary>
public readonly struct Vec3
{
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public double X { get; }
    public double Y { get; }
    public double Z { get; }

    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);

    public double Dot(Vec3 o) => X * o.X + Y * o.Y + Z * o.Z;
    public Vec3 Cross(Vec3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);
    public double Length => Math.Sqrt(Dot(this));
    public bool IsFinite => IsFiniteValue(X) && IsFiniteValue(Y) && IsFiniteValue(Z);

    static bool IsFiniteValue(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>単位ベクトル。長さ0は方向が定義できないため呼び出し側が事前に弾く。</summary>
    public Vec3 Normalized()
    {
        double len = Length;
        if (len <= 0)
            throw new ArgumentException("長さ0のベクトルは正規化できない（方向が定義されない）");
        return new Vec3(X / len, Y / len, Z / len);
    }

    public override string ToString() =>
        FormattableString.Invariant($"({X:F6},{Y:F6},{Z:F6})");
}

/// <summary>
/// 窓 pick 時点の座標系。各ポートが CAD SDK から中立値へ移して渡す。
/// UCS 軸は UCS→WCS の基底（原点＋三軸）。ビューの目標点・視線方向は WCS
/// （実測確認済み: ViewTableRecord の Target/ViewDirection は WCS、システム変数 TARGET は UCS。
/// 両者を取り違えると変換が壊れるため、ポートは ViewTableRecord 側の値を渡すこと）。
/// </summary>
public readonly struct WindowPickFrame
{
    public WindowPickFrame(Vec3 ucsOrigin, Vec3 ucsXAxis, Vec3 ucsYAxis, Vec3 ucsZAxis,
                           Vec3 viewTargetWcs, Vec3 viewDirectionWcs, double viewTwistRad)
    {
        UcsOrigin = ucsOrigin; UcsXAxis = ucsXAxis; UcsYAxis = ucsYAxis; UcsZAxis = ucsZAxis;
        ViewTargetWcs = viewTargetWcs; ViewDirectionWcs = viewDirectionWcs; ViewTwistRad = viewTwistRad;
    }

    public Vec3 UcsOrigin { get; }
    public Vec3 UcsXAxis { get; }
    public Vec3 UcsYAxis { get; }
    public Vec3 UcsZAxis { get; }
    public Vec3 ViewTargetWcs { get; }
    public Vec3 ViewDirectionWcs { get; }
    public double ViewTwistRad { get; }
}

/// <summary>
/// 窓の座標契約。製品ごとに実測して選ぶ（同一の SDK 形状でも契約が同じとは限らないため、
/// 「同じコードだから同じ」を根拠にしない）。規則の実装は WindowPickMapper 1か所に置き、
/// 各ポートは自製品で実測した契約を選ぶだけにする。
/// </summary>
public enum WindowPickContract
{
    /// <summary>
    /// ビューの DCS で指定する。**AutoCAD 2026 実機で確定**（tests/tools/regression/window_contract）。
    /// CAD 自身の -PLOT が格納する窓と、対照との画素一致の双方で裏付けた。
    /// </summary>
    Dcs,

    /// <summary>
    /// 現在 UCS の点を WCS まで写す。全対応製品の実機契約は DCS と確定済みのため製品既定値には
    /// 使わないが、変換段階の独立検査と過去成果物の診断比較のために残す。
    /// </summary>
    UcsToWcs,
}

/// <summary>窓 pick の写像結果の種別。</summary>
public enum WindowPickOutcome
{
    /// <summary>窓を DCS で確定できた。</summary>
    Ok,

    /// <summary>
    /// UCS 平面が視線と平行で、窓が線に潰れる。投影が定義できず、この向きのままでは
    /// 窓プロットが成立しない（CAD 自身も中止する）。
    /// </summary>
    ViewPlaneDegenerate,
}

/// <summary>窓 pick の写像結果。</summary>
public readonly struct WindowPickResult
{
    WindowPickResult(WindowPickOutcome outcome, PlotWindow window)
    {
        Outcome = outcome; Window = window;
    }

    public WindowPickOutcome Outcome { get; }

    /// <summary><see cref="WindowPickOutcome.Ok"/> のときだけ意味を持つ。</summary>
    public PlotWindow Window { get; }

    public static WindowPickResult OfWindow(PlotWindow window) => new(WindowPickOutcome.Ok, window);
    public static WindowPickResult OfViewPlaneDegenerate() => new(WindowPickOutcome.ViewPlaneDegenerate, default);
}

public static class WindowPickMapper
{
    /// <summary>
    /// 任意軸アルゴリズムの分岐しきい値。法線の X・Y 成分がともにこの値未満なら、
    /// 基準軸に世界 Y 軸を使う（それ以外は世界 Z 軸）。DXF 仕様が定める値であり、
    /// この値でのみ CAD の DCS 基底と一致する（実測で突き合わせ済み）。
    /// </summary>
    const double ArbitraryAxisThreshold = 1.0 / 64.0;

    /// <summary>
    /// UCS 平面を真横から見ている（視線が平面に含まれる）とみなす、法線と視線の内積の上限。
    /// この向きでは pick 点がすべて DCS 上の1直線へ潰れ、窓が面積を持てない。
    /// 丸め誤差だけを吸収する値にする。「平行に近いが表現はできる」向きは窓が小さくなるだけなので、
    /// 小さすぎる窓を許すかどうかの方針は呼び出し側（RangePrompt の縮退窓判定）に委ねる。
    /// </summary>
    const double ViewPlaneEdgeOnTolerance = 1e-12;

    /// <summary>CAD SDK が返す UCS の正規直交基底を検査するときの丸め許容差。</summary>
    const double OrthonormalTolerance = 1e-9;

    /// <summary>
    /// pick した2点（現在 UCS）を、窓の座標契約へ写した軸平行矩形にする。
    /// 2点の順序（利用者のドラッグ方向）に依らず同じ矩形になるよう min/max で正規化する
    /// （どの象限へドラッグしても同一の窓になる）。
    /// </summary>
    public static WindowPickResult ToPlotWindow(Vec3 pick1Ucs, Vec3 pick2Ucs,
                                                WindowPickFrame frame, WindowPickContract contract)
    {
        ValidateInputs(pick1Ucs, pick2Ucs, frame);

        // 視線と UCS 平面の関係は pick 点に依らないフレームの性質なので、写す前に判定する
        if (contract == WindowPickContract.Dcs && IsViewPlaneEdgeOn(frame))
            return WindowPickResult.OfViewPlaneDegenerate();

        var p1 = MapPoint(pick1Ucs, frame, contract);
        var p2 = MapPoint(pick2Ucs, frame, contract);
        return WindowPickResult.OfWindow(new PlotWindow(
            Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y),
            Math.Max(p1.X, p2.X), Math.Max(p1.Y, p2.Y)));
    }

    static void ValidateInputs(Vec3 pick1Ucs, Vec3 pick2Ucs, WindowPickFrame frame)
    {
        if (!pick1Ucs.IsFinite || !pick2Ucs.IsFinite)
            throw new ArgumentException($"pick 点に有限でない座標がある: {pick1Ucs}, {pick2Ucs}");
        if (!frame.UcsOrigin.IsFinite || !frame.UcsXAxis.IsFinite
            || !frame.UcsYAxis.IsFinite || !frame.UcsZAxis.IsFinite
            || !frame.ViewTargetWcs.IsFinite || !frame.ViewDirectionWcs.IsFinite
            || double.IsNaN(frame.ViewTwistRad) || double.IsInfinity(frame.ViewTwistRad))
            throw new ArgumentException("座標変換フレームに有限でない値がある");

        var x = frame.UcsXAxis;
        var y = frame.UcsYAxis;
        var z = frame.UcsZAxis;
        if (x.Length <= 0 || y.Length <= 0 || z.Length <= 0)
            throw new ArgumentException("UCS 基底軸に長さ0のベクトルがある");
        if (frame.ViewDirectionWcs.Length <= 0)
            throw new ArgumentException("ビューの視線方向が長さ0である");

        var xn = x.Normalized();
        var yn = y.Normalized();
        var zn = z.Normalized();
        if (Math.Abs(x.Length - 1) > OrthonormalTolerance
            || Math.Abs(y.Length - 1) > OrthonormalTolerance
            || Math.Abs(z.Length - 1) > OrthonormalTolerance
            || Math.Abs(xn.Dot(yn)) > OrthonormalTolerance
            || Math.Abs(yn.Dot(zn)) > OrthonormalTolerance
            || Math.Abs(zn.Dot(xn)) > OrthonormalTolerance
            || (xn.Cross(yn) - zn).Length > OrthonormalTolerance)
            throw new ArgumentException("UCS 基底軸が右手系の正規直交基底でない");
    }

    static bool IsViewPlaneEdgeOn(WindowPickFrame frame)
    {
        var normal = frame.UcsZAxis;
        var dir = frame.ViewDirectionWcs;
        return Math.Abs(normal.Normalized().Dot(dir.Normalized())) < ViewPlaneEdgeOnTolerance;
    }

    static Vec3 MapPoint(Vec3 ucsPoint, WindowPickFrame frame, WindowPickContract contract) =>
        contract switch
        {
            WindowPickContract.Dcs => UcsToDcs(ucsPoint, frame),
            WindowPickContract.UcsToWcs => UcsToWcs(ucsPoint, frame),
            _ => throw new NotSupportedException($"未知の窓座標契約: {contract}"),
        };

    static Vec3 UcsToWcs(Vec3 ucsPoint, WindowPickFrame frame) =>
        frame.UcsOrigin
        + frame.UcsXAxis * ucsPoint.X
        + frame.UcsYAxis * ucsPoint.Y
        + frame.UcsZAxis * ucsPoint.Z;

    /// <summary>pick 点（UCS）→ WCS → DCS。DCS の Z は窓に使わないが、写像の定義として計算する。</summary>
    static Vec3 UcsToDcs(Vec3 ucsPoint, WindowPickFrame frame)
    {
        var wcs = UcsToWcs(ucsPoint, frame);
        var dir = frame.ViewDirectionWcs.Normalized();
        // ビューのねじれを戻してから目標点を原点へ移し、DCS の基底へ射影する
        // （DCS→WCS が「ねじれ→目標点変位→視線方向の平面→世界」である写像の逆順）。
        var rotated = RotateAbout(wcs, frame.ViewTargetWcs, dir, frame.ViewTwistRad);
        var rel = rotated - frame.ViewTargetWcs;
        var (ax, ay) = ArbitraryAxes(dir);
        return new Vec3(rel.Dot(ax), rel.Dot(ay), rel.Dot(dir));
    }

    /// <summary>点を、指定した点を通る軸まわりに回す（ロドリゲスの回転公式）。</summary>
    static Vec3 RotateAbout(Vec3 point, Vec3 axisOrigin, Vec3 axisUnit, double angleRad)
    {
        if (angleRad == 0) return point;
        var v = point - axisOrigin;
        double cos = Math.Cos(angleRad), sin = Math.Sin(angleRad);
        var rotated = v * cos
                    + axisUnit.Cross(v) * sin
                    + axisUnit * (axisUnit.Dot(v) * (1 - cos));
        return axisOrigin + rotated;
    }

    /// <summary>
    /// 任意軸アルゴリズム: 法線から DCS の X・Y 基底を決める。CAD の DCS 基底と一致する
    /// （3Dビューを含む実機値と突き合わせ済み）。
    /// </summary>
    static (Vec3 AxisX, Vec3 AxisY) ArbitraryAxes(Vec3 normalUnit)
    {
        var worldY = new Vec3(0, 1, 0);
        var worldZ = new Vec3(0, 0, 1);
        var reference = Math.Abs(normalUnit.X) < ArbitraryAxisThreshold
                     && Math.Abs(normalUnit.Y) < ArbitraryAxisThreshold
            ? worldY
            : worldZ;
        var ax = reference.Cross(normalUnit).Normalized();
        var ay = normalUnit.Cross(ax).Normalized();
        return (ax, ay);
    }
}
