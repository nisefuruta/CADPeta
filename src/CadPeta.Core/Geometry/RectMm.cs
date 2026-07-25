// 軸平行矩形（mm）。事前クリップ（EMF出力）とクロップ変換が共用する。
namespace CadPeta.Geometry;

public readonly record struct RectMm(double X0, double Y0, double X1, double Y1)
{
    const double Eps = 1e-9; // 幾何判定の数値誤差吸収（mm。コンテンツ量子 0.0106mm より十分小さい）

    public bool ContainsBbox(double[] b) =>
        b[0] >= X0 - Eps && b[1] >= Y0 - Eps && b[2] <= X1 + Eps && b[3] <= Y1 + Eps;
    public bool OverlapsBbox(double[] b) =>
        b[0] <= X1 + Eps && b[2] >= X0 - Eps && b[1] <= Y1 + Eps && b[3] >= Y0 - Eps;

    public static RectMm? Intersect(RectMm a, RectMm b)
    {
        double x0 = Math.Max(a.X0, b.X0), y0 = Math.Max(a.Y0, b.Y0);
        double x1 = Math.Min(a.X1, b.X1), y1 = Math.Min(a.Y1, b.Y1);
        return x0 < x1 && y0 < y1 ? new RectMm(x0, y0, x1, y1) : null;
    }
}
