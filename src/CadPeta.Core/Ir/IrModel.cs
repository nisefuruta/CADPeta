// SVG IR モデル（SVG のサブセットに絞った制約プロファイル）。
// 内核（ドメイン層）に属する。外部ライブラリの型（PdfPig 等）をここに持ち込まない。
//
// 座標系: 単位は mm、Y軸は下向き正（CAD/PDF 座標からの反転は取得側の責務）。
// 描画順（z順）は IrDocument.Nodes のリスト順で保存する（ワイプアウト＝白塗りの成立条件）。
namespace CadPeta.Ir;

using System.Globalization;

/// <summary>RGB色（各成分 0..1）。</summary>
public readonly record struct IrRgb(double R, double G, double B)
{
    public (int R, int G, int B) ToRgb8() =>
        ((int)Math.Round(R * 255), (int)Math.Round(G * 255), (int)Math.Round(B * 255));

    public string ToHex()
    {
        var (r, g, b) = ToRgb8();
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    public string ToSvg()
    {
        var (r, g, b) = ToRgb8();
        return $"rgb({r},{g},{b})";
    }

    public static bool TryParseHex(string? value, out IrRgb color)
    {
        color = default;
        if (value is null || value.Length != 7 || value[0] != '#'
            || !int.TryParse(value.Substring(1), NumberStyles.HexNumber,
                CultureInfo.InvariantCulture, out int rgb))
            return false;
        const double componentMax = 255.0;
        color = new IrRgb(((rgb >> 16) & 0xFF) / componentMax,
            ((rgb >> 8) & 0xFF) / componentMax, (rgb & 0xFF) / componentMax);
        return true;
    }

    public static IrRgb ParseHex(string value) => TryParseHex(value, out var color)
        ? color
        : throw new ArgumentException($"色は #RRGGBB 形式で指定する（例: #FF0000）: {value}");
}

/// <summary>パスセグメント（mm座標）。path の d 属性に対応する。</summary>
public abstract record IrSegment;
public sealed record IrMove(double X, double Y) : IrSegment;
public sealed record IrLine(double X, double Y) : IrSegment;
public sealed record IrCubic(double X1, double Y1, double X2, double Y2, double X, double Y) : IrSegment;
public sealed record IrClose : IrSegment;

/// <summary>描画ノード共通。出所メタデータは data-* 属性として保持する。</summary>
public abstract class IrNode
{
    /// <summary>元レイヤ名（data-layer）。PDF印刷経路ではPDFのOCGから得る。不明なら null。</summary>
    public string? DataLayer;

    /// <summary>色の指定方法（data-color-method）: aci / truecolor / bylayer / byblock。
    /// ByLayer/ByBlock は色を実効値へ解決した上で、解決前の指定方法をここに残す。
    /// PDF印刷経路（プロット経由）では判別できないため null。</summary>
    public string? DataColorMethod;

    /// <summary>ACI 番号（data-aci、1〜255）。ByLayer/ByBlock は解決後の値。
    /// TrueColor 指定・PDF印刷経路では null。</summary>
    public int? DataAci;

    /// <summary>元エンティティ種別（data-entity、診断用）。PDF印刷経路では null。</summary>
    public string? DataEntity;

    /// <summary>適用するクリップの id（IrDocument.ClipDefs を参照）。なければ null。</summary>
    public string? ClipId;
}

/// <summary>2Dアフィン変換。(x,y) → (A·x + C·y + E, B·x + D·y + F)。
/// 成分の並びは SVG の transform="matrix(a,b,c,d,e,f)" と同一。</summary>
public readonly record struct IrMatrix(double A, double B, double C, double D, double E, double F)
{
    public static readonly IrMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public bool IsIdentity => this == Identity;

    /// <summary>合成 this∘other（other を先に適用）。</summary>
    public IrMatrix Multiply(IrMatrix o) => new(
        A * o.A + C * o.B,
        B * o.A + D * o.B,
        A * o.C + C * o.D,
        B * o.C + D * o.D,
        A * o.E + C * o.F + E,
        B * o.E + D * o.F + F);

    public (double X, double Y) Apply(double x, double y) =>
        (A * x + C * y + E, B * x + D * y + F);

    public double Det => A * D - B * C;

    /// <summary>面積比の平方根＝等方成分の代表スケール。線幅・フォント寸法の変換に使う。</summary>
    public double ScaleFactor => Math.Sqrt(Math.Abs(Det));

    // 最大線形成分で正規化した行列式の許容値。座標単位や一様尺度に依存せず、
    // double の丸め誤差を十分上回る悪条件変換だけを拒否する。
    const double RelativeInverseDeterminantTolerance = 1e-12;

    /// <summary>逆アフィン変換。非有限または相対的に特異な行列なら false を返す。</summary>
    public bool TryInverse(out IrMatrix inverse)
    {
        if (!Finite(A) || !Finite(B) || !Finite(C) || !Finite(D) || !Finite(E) || !Finite(F))
        {
            inverse = default;
            return false;
        }
        double scale = Math.Max(Math.Max(Math.Abs(A), Math.Abs(B)), Math.Max(Math.Abs(C), Math.Abs(D)));
        if (!(scale > 0) || !Finite(scale))
        {
            inverse = default;
            return false;
        }
        double normalizedDet = (A / scale) * (D / scale) - (B / scale) * (C / scale);
        if (!Finite(normalizedDet) || Math.Abs(normalizedDet) <= RelativeInverseDeterminantTolerance)
        {
            inverse = default;
            return false;
        }
        double det = Det;
        inverse = new IrMatrix(D / det, -B / det, -C / det, A / det,
            (C * F - D * E) / det, (B * E - A * F) / det);
        if (Finite(inverse.A) && Finite(inverse.B) && Finite(inverse.C)
            && Finite(inverse.D) && Finite(inverse.E) && Finite(inverse.F)) return true;
        inverse = default;
        return false;
    }

    static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    /// <summary>相似変換（等方スケール×回転。反転を含む）か。真なら線幅・文字が歪みなく写る。
    /// 列ベクトル長の一致と直交で判定する（許容は相対 1e-9: 行列演算の丸め誤差レベル）。</summary>
    public bool IsSimilarity
    {
        get
        {
            double l1 = A * A + B * B, l2 = C * C + D * D, dot = A * C + B * D;
            double tol = 1e-9 * Math.Max(l1, l2);
            return Math.Abs(l1 - l2) <= tol && Math.Abs(dot) <= tol;
        }
    }
}

/// <summary>g 要素（変換行列付きグループ）。ブロック参照の展開先で、
/// 子ノードの座標はグループローカル（Transform 適用前）の mm 値を持つ。</summary>
public sealed class IrGroup : IrNode
{
    public IrMatrix Transform = IrMatrix.Identity;
    public List<IrNode> Children = new();
}

/// <summary>path 要素。線分・円弧（ベジェ）・ハッチ輪郭。</summary>
public sealed class IrPath : IrNode
{
    public List<IrSegment> Segments = new();
    public IrRgb? Stroke;                 // null = stroke なし
    public double StrokeWidthMm;
    /// <summary>破線パターン（stroke-dasharray、mm。実線＋間隙の交互列）。実線なら null。
    /// PETADATAの構造SVG書き出しでは線種から得る。PDF印刷経路ではプロットが破線をセグメント群へ展開済みのため常に null。
    /// 構造SVG書き出しは端部整列（パターンジャスティフィケーション、docs/02 §2）を済ませた配列を持ち、
    /// 開いた曲線は両端の実線片が調整済み・閉じた曲線は整数周期へ伸縮済みの値になる。</summary>
    public double[]? StrokeDashMm;
    /// <summary>破線パターンの位相オフセット（stroke-dashoffset、mm）。既定0。
    /// 端部整列は StrokeDashMm の値自体を伸縮して表現するため、構造SVG書き出しは現状常に0を置く
    /// （将来の拡張点として保持する）。</summary>
    public double StrokeDashOffsetMm;
    public double StrokeOpacity = 1.0;    // PDF CA
    public IrRgb? Fill;                   // null = fill なし
    public bool FillEvenOdd;              // fill-rule="evenodd"
    public double FillOpacity = 1.0;      // PDF ca
}

/// <summary>text 要素（フォント保持の場合）。</summary>
public sealed class IrText : IrNode
{
    public string Value = "";
    public double XMm, YMm;               // ベースライン原点
    public string FontFamily = "";
    public double FontSizeMm;
    /// <summary>幅係数（PDF Tz 水平スケーリング → 係数。1.0 が標準）。</summary>
    public double WidthFactor = 1.0;
    /// <summary>回転角（度、IR座標系=Y下向きで時計回り正。0 が水平）。
    /// PDF印刷経路ではPDFのベースライン方向から得る（回転寸法値の類型、実物図面で実測）。</summary>
    public double RotationDeg;
    /// <summary>グリフごとのX位置（mm）。回転時はベースライン方向への射影距離を XMm 起点で持つ。
    /// null なら Value 全体を XMm から自然送りで置く。</summary>
    public double[]? GlyphXsMm;
    public IrRgb? Fill;
    public double FillOpacity = 1.0;
}

/// <summary>image 要素。図面内ラスタ画像の位置保持。href に埋め込む。</summary>
public sealed class IrImage : IrNode
{
    public double XMm, YMm, WidthMm, HeightMm;
    public byte[] PngBytes = Array.Empty<byte>();
}

/// <summary>clipPath 定義。ビューポート境界・ワイプアウト用。</summary>
public sealed class IrClipDef
{
    public string Id = "";
    public List<IrSegment> Segments = new();
    public bool EvenOdd;
}

/// <summary>レイヤ別グループ化のグループ境界。Nodes の先頭から Count 個ずつを
/// 1グループとして g 要素へ直列化する（設定は LayerGroupTransform、消費は SvgWriter）。</summary>
public sealed record IrLayerGroup(string Label, int Count);

/// <summary>IR文書。物理寸法（mm）が実寸再現の根拠となる。</summary>
public sealed class IrDocument
{
    public double WidthMm, HeightMm;
    /// <summary>viewBox の原点（mm）。クロップ時に切り出し範囲の左上を指す。</summary>
    public double ViewMinXMm, ViewMinYMm;
    public List<IrClipDef> ClipDefs = new();
    public List<IrNode> Nodes = new();    // 描画順（z順）
    /// <summary>レイヤ別グループ化の結果（null=グループ化なし＝既定）。設定時は Count の総和が
    /// Nodes.Count と一致する規約で、SvgWriter が直列化前に検査する。グループ化は変換
    /// パイプラインの最終段のため、以降に Nodes を増減する処理を置かない。</summary>
    public List<IrLayerGroup>? LayerGroups;
}
