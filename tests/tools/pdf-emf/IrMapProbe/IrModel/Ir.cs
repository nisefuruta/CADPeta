// SVG IR モデル（設計書§2 制約プロファイルの部分実装、Phase 0 予行）
// 内核（ドメイン層）に属する。外部ライブラリの型（PdfPig 等）をここに持ち込まない（設計書§1.1）。
//
// 座標系は §2.2 に従う: 単位は mm、Y軸は下向き正（CAD/PDF 座標からの反転は取得側の責務）。
// 描画順（z順）は IrDocument.Nodes のリスト順で保存する（ワイプアウト＝白塗りの成立条件）。
namespace CadPeta.Ir;

/// <summary>RGB色（各成分 0..1）。</summary>
public readonly record struct IrRgb(double R, double G, double B)
{
    public string ToSvg() =>
        $"rgb({(int)Math.Round(R * 255)},{(int)Math.Round(G * 255)},{(int)Math.Round(B * 255)})";
}

/// <summary>パスセグメント（mm座標）。path の d 属性に対応する。</summary>
public abstract record IrSegment;
public sealed record IrMove(double X, double Y) : IrSegment;
public sealed record IrLine(double X, double Y) : IrSegment;
public sealed record IrCubic(double X1, double Y1, double X2, double Y2, double X, double Y) : IrSegment;
public sealed record IrClose : IrSegment;

/// <summary>描画ノード共通。出所メタデータは data-* 属性として保持する（設計書§2.3）。</summary>
public abstract class IrNode
{
    /// <summary>元レイヤ名（data-layer）。PDF印刷経路ではPDFのOCGから得る。不明なら null。</summary>
    public string? DataLayer;

    /// <summary>適用するクリップの id（IrDocument.ClipDefs を参照）。なければ null。</summary>
    public string? ClipId;
}

/// <summary>path 要素。線分・円弧（ベジェ）・ハッチ輪郭（設計書§2.1）。</summary>
public sealed class IrPath : IrNode
{
    public List<IrSegment> Segments = new();
    public IrRgb? Stroke;                 // null = stroke なし
    public double StrokeWidthMm;
    public double StrokeOpacity = 1.0;    // PDF CA
    public IrRgb? Fill;                   // null = fill なし
    public bool FillEvenOdd;              // fill-rule="evenodd"
    public double FillOpacity = 1.0;      // PDF ca
}

/// <summary>text 要素（フォント保持の場合、設計書§2.1）。</summary>
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

/// <summary>image 要素。図面内ラスタ画像の位置保持（設計書§2.1）。href に埋め込む。</summary>
public sealed class IrImage : IrNode
{
    public double XMm, YMm, WidthMm, HeightMm;
    public byte[] PngBytes = Array.Empty<byte>();
}

/// <summary>clipPath 定義。ビューポート境界・ワイプアウト用（設計書§2.1）。</summary>
public sealed class IrClipDef
{
    public string Id = "";
    public List<IrSegment> Segments = new();
    public bool EvenOdd;
}

/// <summary>IR文書。物理寸法（mm）が実寸再現（FR-6）の根拠となる（設計書§2.2）。</summary>
public sealed class IrDocument
{
    public double WidthMm, HeightMm;
    /// <summary>viewBox の原点（mm）。クロップ時に図形範囲の左上を指す（設計書§2.2）。</summary>
    public double ViewMinXMm, ViewMinYMm;
    public List<IrClipDef> ClipDefs = new();
    public List<IrNode> Nodes = new();    // 描画順（z順）
}
