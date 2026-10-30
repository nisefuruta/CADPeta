// SHX グリフのバイトコード解釈（命令 0-14）。
// 実装メモ（拡張ビッグフォント code7,0 の配置・スケーリング意味論の出典）:
//   一次情報は Autodesk 公式ドキュメント "Special Codes Reference" 系列
//   （例: https://documentation.help/AutoCAD-ACG/WS73099cc142f4875513fb5cd10c4aa30d6b-7f0e.htm
//    "Code 00A: Octant Arc" — 半径・方向/開始オクタント/オクタント数のビット割り付け、
//    および同系列 "Code 00B: Fractional Arc" のオフセット計算式・"Code 00C: Bulge-Specified Arc"
//    のバルジ定義 (2H/D)*127）を用い、算出式は本ファイルで独立に導出・実装した。
//   拡張ビッグフォントの basepoint(bx,by)+width/height によるプリミティブ配置・非一様スケール
//   （プリミティブ自身のバウンディングボックスを対象幅高さへ合わせる）の意味論は、OSS実装
//   mlightcad/shx-parser（GitHub, https://github.com/mlightcad/shx-parser、TypeScript製）の
//   ソース（shapeParser.ts の handleSubshapeCommand/getScaledSubshapeAtInsertPoint/
//   scaleShapeByHeightAndWidth 相当箇所）を読み、意味論のみを抽出して本ファイルで独立に
//   C# 実装した（コードはコピーしない）。同ライブラリの「素の bigfont 参照（拡張マーカーでない
//   1バイト形式）も常にバウンディングボックス比でスケールする」という挙動は、公式資料での
//   裏付けを確認できなかったため採用せず、本実装ではタスク仕様どおり「現在のペン位置・スケール・
//   ペン状態を継承した単純な再帰描画」とした（この判断も含めて実装メモとして報告する）。
//   opcode 11（フラクショナル弧）・13（複数バルジ弧）は本機実物 27,373 グリフで一度も出現せず
//   （docs/validation/architecture-decisions.md §2.3）、11 は Autodesk 公式資料の1例（55°→95°の変換例）のみを
//   根拠に独立に式を導出したため信頼度が低いことを明記する。
using System;
using System.Collections.Generic;
using System.IO;
using CadPeta.Ir;

namespace CadPeta.Glyphs.Shx;

/// <summary>
/// グリフ1個ぶんのバイトコードを、フォント単位・Y上向き・原点=ペン開始位置の
/// IrSegment 列（開いたパスの並び。IrMove で新しい部分パスを開始し IrLine/IrCubic で延ばす。
/// IrClose は使わない）へ展開する。
/// </summary>
sealed class ShxGlyphWalker
{
    // ---- 命令の意味論に現れる定数（マジックナンバー禁止の適用） ----

    /// <summary>1オクタント=45°。オクタント弧命令（code10）の角度単位。</summary>
    const double OctantRadians = Math.PI / 4.0;

    /// <summary>円弧を3次ベジェへ分割する上限角（90°）。この角度以下ならベジェ近似の誤差が
    /// 実用上無視できる（4分円=90°で最大誤差は半径比0.03%未満の既知の事実）。</summary>
    const double MaxArcSpanRadians = Math.PI / 2.0;

    /// <summary>バルジバイトの正規化除数。AutoCAD シェイプ記述の規約で「半円=127」と定義される
    /// （bulge/127 = tan(内角/4)。Autodesk 公式資料 "Code 00C: Bulge-Specified Arc"）。</summary>
    const double BulgeUnit = 127.0;

    /// <summary>サブシェイプ呼出の再帰段数上限。循環参照・異常データによる
    /// スタックオーバーフローを防ぐための安全弁（実物フォントの最大観測ネストは数段）。</summary>
    const int MaxSubshapeDepth = 64;

    /// <summary>方向コード(0-15)→単位ベクトル。vector命令（16-255）の下位4bitで引く。
    /// 45°刻みの8方向うち斜め方向は主軸成分を0.5倍にする既定の符号化（実測値と一致、
    /// tests/tools/cad-typography/shx_binary_survey.py の VECTOR_DIR と同一の規約）。</summary>
    static readonly (double X, double Y)[] VectorDir =
    {
        (1, 0), (1, 0.5), (1, 1), (0.5, 1),
        (0, 1), (-0.5, 1), (-1, 1), (-1, 0.5),
        (-1, 0), (-1, -0.5), (-1, -1), (-0.5, -1),
        (0, -1), (0.5, -1), (1, -1), (1, -0.5),
    };

    readonly ShxFontKind _fontKind;
    readonly bool _isExtended;
    readonly Func<int, byte[]?> _resolveGlyph;

    public ShxGlyphWalker(ShxFontKind fontKind, bool isExtended, Func<int, byte[]?> resolveGlyph)
    {
        _fontKind = fontKind;
        _isExtended = isExtended;
        _resolveGlyph = resolveGlyph;
    }

    /// <summary>グリフ1個を解釈し、セグメント列と終端カーソル位置（フォント単位）を返す。
    /// initialPenDown は呼出し前のペン状態（既定 false=ペンアップから開始）。
    /// 文字グリフは自身の先頭でペンダウンを発行するため既定のままでよいが、複雑線種の
    /// シェイプ記号（TRACK1/ZIG/BOX 等）は呼出元がペンダウン済みである前提で自身は
    /// ペンダウンを発行しない（実測: ShxShapesFormatTests のサブシェイプ呼出テスト参照）。
    /// そのため線種記号を直接1個だけ描画する呼び出しは initialPenDown=true を渡す。</summary>
    public (List<IrSegment> Segments, double EndX, double EndY) Walk(byte[] bytecode,
                                                                     bool initialPenDown = false)
    {
        var state = new PenState();
        var segs = new List<IrSegment>();
        if (initialPenDown)
        {
            // code1（ペンダウン）の実処理と同じく、開始点(0,0)をまず部分パスの起点として
            // 記録する。これを省くと最初の変位命令が「新しい部分パスの開始（IrMove）」に
            // なってしまい、原点からの最初の線分が描画されない
            state.PenDown = true;
            EmitPoint(state, segs);
        }
        WalkInto(bytecode, state, segs, depth: 0);
        return (segs, state.X, state.Y);
    }

    /// <summary>グリフ描画中の可変状態（ペン位置・スケール・ペン状態・部分パス継続フラグ）。
    /// サブシェイプ呼出（拡張ビッグフォントを除く）は同一インスタンスを引き継いで再帰する。</summary>
    sealed class PenState
    {
        public double X, Y;
        public double Scale = 1.0;
        public bool PenDown;
        /// <summary>直近に IrMove で部分パスを開始済みで、次の描画点は IrLine/IrCubic で
        /// 継続すべきか。false なら次の描画点が新しい部分パスの開始（IrMove）になる。</summary>
        public bool HasOpenSubpath;
        public readonly Stack<(double X, double Y)> PositionStack = new();
    }

    // -----------------------------------------------------------------
    // メインループ
    // -----------------------------------------------------------------

    void WalkInto(byte[] data, PenState st, List<IrSegment> segs, int depth)
    {
        if (depth > MaxSubshapeDepth)
            throw new InvalidDataException(
                $"サブシェイプ呼出の再帰が{MaxSubshapeDepth}段を超えた（循環参照の疑い）");

        int i = 0;
        int n = data.Length;
        while (i < n)
        {
            int cb = data[i];
            if (cb <= 14)
            {
                switch (cb)
                {
                    case 0:
                        // グリフを終端しない。ペン状態リセットの副作用（部分パスの区切り）のみ。
                        // グリフ境界は索引の length だけが決める。
                        i++;
                        st.HasOpenSubpath = false;
                        break;
                    case 1: // ペンダウン
                        i++;
                        if (!st.PenDown)
                        {
                            st.PenDown = true;
                            EmitPoint(st, segs);
                        }
                        break;
                    case 2: // ペンアップ
                        i++;
                        st.PenDown = false;
                        st.HasOpenSubpath = false;
                        break;
                    case 3: // スケール除算
                        i++;
                        RequireBytes(data, i, 1, "code3(スケール除算)");
                        {
                            int denom = data[i]; i++;
                            if (denom == 0)
                                throw new InvalidDataException("code3(スケール除算) の除数が0");
                            st.Scale /= denom;
                        }
                        break;
                    case 4: // スケール乗算
                        i++;
                        RequireBytes(data, i, 1, "code4(スケール乗算)");
                        { int mul = data[i]; i++; st.Scale *= mul; }
                        break;
                    case 5: // 位置push
                        i++;
                        st.PositionStack.Push((st.X, st.Y));
                        break;
                    case 6: // 位置pop
                        // pushされていない位置をpopしようとした場合は何もしない（現在位置を保つ）。
                        // 実物フォント（bigfont系4ファイルのcode=0x000d等）で実際に使われる正常な
                        // パターンであり不正データではないことを実測で確認済み。
                        i++;
                        if (st.PositionStack.Count > 0)
                            (st.X, st.Y) = st.PositionStack.Pop();
                        st.HasOpenSubpath = false;
                        if (st.PenDown) EmitPoint(st, segs);
                        break;
                    case 7:
                        i = HandleSubshape(data, i, st, segs, depth);
                        break;
                    case 8: // XY変位1組
                        i++;
                        RequireBytes(data, i, 2, "code8(XY変位)");
                        {
                            double dx = ToSByte(data[i]) * st.Scale;
                            double dy = ToSByte(data[i + 1]) * st.Scale;
                            i += 2;
                            st.X += dx; st.Y += dy;
                            if (st.PenDown) EmitPoint(st, segs);
                        }
                        break;
                    case 9: // XY変位列、(0,0)で終端
                        i++;
                        while (ReadDisplacement(data, ref i, "code9(XY変位列)", out int rdx, out int rdy))
                        {
                            st.X += ToSByte(rdx) * st.Scale;
                            st.Y += ToSByte(rdy) * st.Scale;
                            if (st.PenDown) EmitPoint(st, segs);
                        }
                        break;
                    case 10:
                        i = HandleOctantArc(data, i, st, segs);
                        break;
                    case 11:
                        i = HandleFractionalArc(data, i, st, segs);
                        break;
                    case 12:
                        i = HandleBulgeArc(data, i, st, segs);
                        break;
                    case 13:
                        i = HandleBulgeArcSequence(data, i, st, segs);
                        break;
                    case 14: // 縦書き分岐。水平レイアウト固定のため次の1命令を読み飛ばす
                        i++;
                        i = SkipOne(data, i);
                        break;
                }
            }
            else
            {
                i++;
                int length = (cb >> 4) & 0xF;
                int dir = cb & 0xF;
                var (ux, uy) = VectorDir[dir];
                st.X += ux * length * st.Scale;
                st.Y += uy * length * st.Scale;
                if (st.PenDown) EmitPoint(st, segs);
            }
        }
    }

    /// <summary>符号なしバイトを SHX 流の符号付きバイトへ変換する（0x80ビットが立っていれば128を引く）。</summary>
    static int ToSByte(int v) => (v & 0x7F) - (v & 0x80);

    static void RequireBytes(byte[] data, int pos, int n, string context)
    {
        if (pos < 0 || pos + n > data.Length)
            throw new InvalidDataException(
                $"{context}: 必要な{n}バイトに対しデータが不足（位置{pos}、データ長{data.Length}）");
    }

    /// <summary>現在のペン位置を1点、部分パスへ追加する（IrMove=開始 / IrLine=継続）。
    /// 呼び出し前に st.PenDown を確認すること。</summary>
    static void EmitPoint(PenState st, List<IrSegment> segs)
    {
        if (!st.HasOpenSubpath)
        {
            segs.Add(new IrMove(st.X, st.Y));
            st.HasOpenSubpath = true;
        }
        else
        {
            segs.Add(new IrLine(st.X, st.Y));
        }
    }

    // -----------------------------------------------------------------
    // 円弧共通（オクタント弧・フラクショナル弧・バルジ弧が共有する角度→ベジェ展開）
    // -----------------------------------------------------------------

    /// <summary>中心(cx,cy)・半径r・開始角/終了角（ラジアン、CCW正）の円弧を、90°以下の
    /// 区間へ分割して3次ベジェで描画する。呼び出し時点で st.X,st.Y は弧の始点と一致していること。
    /// ペンアップ中は描画せずカーソル位置だけ弧の終点へ進める。</summary>
    static void EmitArc(double cx, double cy, double r, double startAngle, double endAngle,
                        PenState st, List<IrSegment> segs)
    {
        double total = endAngle - startAngle;
        if (!st.PenDown || Math.Abs(total) < 1e-12)
        {
            st.X = cx + r * Math.Cos(endAngle);
            st.Y = cy + r * Math.Sin(endAngle);
            return;
        }

        if (!st.HasOpenSubpath)
        {
            segs.Add(new IrMove(st.X, st.Y));
            st.HasOpenSubpath = true;
        }

        int pieces = Math.Max(1, (int)Math.Ceiling(Math.Abs(total) / MaxArcSpanRadians - 1e-9));
        double step = total / pieces;
        double a0 = startAngle;
        for (int k = 0; k < pieces; k++)
        {
            double a1 = a0 + step;
            // 単一円弧片を1個の3次ベジェで近似する標準式（tan(区間角/4) による接線長）。
            double kFactor = (4.0 / 3.0) * Math.Tan(step / 4.0);
            double p0x = cx + r * Math.Cos(a0), p0y = cy + r * Math.Sin(a0);
            double p1x = cx + r * Math.Cos(a1), p1y = cy + r * Math.Sin(a1);
            double c1x = p0x - kFactor * r * Math.Sin(a0), c1y = p0y + kFactor * r * Math.Cos(a0);
            double c2x = p1x + kFactor * r * Math.Sin(a1), c2y = p1y - kFactor * r * Math.Cos(a1);
            segs.Add(new IrCubic(c1x, c1y, c2x, c2y, p1x, p1y));
            a0 = a1;
        }
        st.X = cx + r * Math.Cos(endAngle);
        st.Y = cy + r * Math.Sin(endAngle);
    }

    // -----------------------------------------------------------------
    // code10: オクタント弧
    // -----------------------------------------------------------------

    static int HandleOctantArc(byte[] data, int i, PenState st, List<IrSegment> segs)
    {
        i = ArgumentsStart(data, i, 2, "code10(オクタント弧)");
        int radiusByte = data[i];
        int flagByte = data[i + 1];
        i += 2;

        double r = radiusByte * st.Scale;
        var (startOctant, totalOctants, clockwise) = DecodeOctantFlags(flagByte);
        double startAngle = startOctant * OctantRadians;
        EmitArcFromPen(r, startAngle, totalOctants * OctantRadians, clockwise, st, segs);
        return i;
    }

    /// <summary>命令のバイトを読み飛ばし、続く <paramref name="count"/> バイトの引数があることを確かめて、引数の先頭の位置を返す。</summary>
    static int ArgumentsStart(byte[] data, int i, int count, string what)
    {
        RequireBytes(data, i + 1, count, what);
        return i + 1;
    }

    /// <summary>
    /// オクタント弧（code10・code11 共通）のフラグバイト。ビット割り付けは Autodesk 公式資料 "Code 00A: Octant Arc":
    /// bit7=方向（1=時計回り）、bit4-6=開始オクタント(0-7)、bit0-2=オクタント数(0=8個=全周)。
    /// </summary>
    static (int StartOctant, double TotalOctants, bool Clockwise) DecodeOctantFlags(int flagByte)
    {
        int count = flagByte & 0x07;
        return ((flagByte & 0x70) >> 4, count == 0 ? 8 : count, (flagByte & 0x80) != 0);
    }

    /// <summary>現在のペン位置を弧の始点として、半径・開始角・掃引の大きさ・向きから中心を求めて弧を出す。</summary>
    static void EmitArcFromPen(double r, double startAngle, double span, bool clockwise, PenState st, List<IrSegment> segs)
    {
        double endAngle = startAngle + (clockwise ? -span : span);
        double cx = st.X - r * Math.Cos(startAngle);
        double cy = st.Y - r * Math.Sin(startAngle);
        EmitArc(cx, cy, r, startAngle, endAngle, st, segs);
    }

    // -----------------------------------------------------------------
    // code11: フラクショナル弧（本機実物での出現ゼロ。信頼度が低い独自解釈）
    // -----------------------------------------------------------------

    static int HandleFractionalArc(byte[] data, int i, PenState st, List<IrSegment> segs)
    {
        i = ArgumentsStart(data, i, 5, "code11(フラクショナル弧)");
        int startOffset = data[i];
        int endOffset = data[i + 1];
        int highRadius = data[i + 2];
        int radiusLow = data[i + 3];
        int flagByte = data[i + 4];
        i += 5;

        // 半径は上位バイト×256+下位バイト（Autodesk公式資料の記述どおり）。
        double r = (highRadius * 256 + radiusLow) * st.Scale;
        var (startOctant, totalOctants, clockwise) = DecodeOctantFlags(flagByte);

        // オフセットは「オクタント境界からの度数×256/45」で符号化される（公式資料の例:
        // 55°→95°の弧、開始オクタント境界45°から10°＝(10*256/45)=56、終了は90°境界から
        // 5°＝(5*256/45)=28、という実例のみを根拠に、開始・終了オクタント数と組み合わせる式を
        // 独自に導出した。実物データでの検証ができていない）。
        double frac = OctantRadians / 256.0;
        double startAngle = startOctant * OctantRadians + startOffset * frac;
        double span = totalOctants * OctantRadians + (endOffset - startOffset) * frac;
        EmitArcFromPen(r, startAngle, span, clockwise, st, segs);
        return i;
    }

    // -----------------------------------------------------------------
    // code12/13: バルジ指定弧
    // -----------------------------------------------------------------

    static int HandleBulgeArc(byte[] data, int i, PenState st, List<IrSegment> segs)
    {
        i = ArgumentsStart(data, i, 3, "code12(バルジ弧)");
        int rdx = data[i], rdy = data[i + 1];
        int bulgeRaw = ToSByte(data[i + 2]);
        i += 3;
        ApplyBulgeSegment(rdx, rdy, bulgeRaw, st, segs);
        return i;
    }

    static int HandleBulgeArcSequence(byte[] data, int i, PenState st, List<IrSegment> segs)
    {
        i++;
        // (0,0) で終端。終端の組には bulge バイトが無い
        while (ReadDisplacement(data, ref i, "code13(バルジ弧列)", out int rdx, out int rdy))
        {
            RequireBytes(data, i, 1, "code13(バルジ弧列)のbulgeバイト");
            int bulgeRaw = ToSByte(data[i]);
            i += 1;
            ApplyBulgeSegment(rdx, rdy, bulgeRaw, st, segs);
        }
        return i;
    }

    /// <summary>XY 変位列（code9・code13）の 1 組（符号付きバイト 2 つ）を読み、i を進める。(0,0) は列の終端で false を返す。</summary>
    static bool ReadDisplacement(byte[] data, ref int i, string what, out int rdx, out int rdy)
    {
        RequireBytes(data, i, 2, what);
        rdx = data[i];
        rdy = data[i + 1];
        i += 2;
        return !(rdx == 0 && rdy == 0);
    }

    /// <summary>変位(dx,dy)とバルジ値からペンを1区間進める。bulge=0 は直線（バルジ=0は直線という
    /// 仕様どおり）。バルジ→円弧の変換は DXF ポリラインのバルジと同一の標準幾何
    /// （bulge=tan(内角/4)、半円=1.0 に相当するバイト値127）で、本ファイルで独立に導出した。
    /// 変位はファイルの符号付きバイトのまま受け取り、現在のスケールを掛ける。</summary>
    static void ApplyBulgeSegment(int rdx, int rdy, int bulgeRaw, PenState st, List<IrSegment> segs)
    {
        double dx = ToSByte(rdx) * st.Scale, dy = ToSByte(rdy) * st.Scale;
        double newX = st.X + dx, newY = st.Y + dy;
        if (bulgeRaw == 0)
        {
            st.X = newX; st.Y = newY;
            if (st.PenDown) EmitPoint(st, segs);
            return;
        }

        double bulge = bulgeRaw / BulgeUnit;
        double theta = 4.0 * Math.Atan(bulge); // 弦の内角（符号=方向。正=反時計回り）
        double d = Math.Sqrt(dx * dx + dy * dy);
        if (d < 1e-12) { st.X = newX; st.Y = newY; return; } // 変位ゼロは縮退として無視

        double halfAbsTheta = Math.Abs(theta) / 2.0;
        double r = d / (2.0 * Math.Sin(halfAbsTheta));
        double apothem = r * Math.Cos(halfAbsTheta); // 弦の中点から中心までの距離
        double mx = (st.X + newX) / 2.0, my = (st.Y + newY) / 2.0;
        double perpX = -dy / d, perpY = dx / d; // 弦を+90°回転した単位ベクトル
        double sign = theta >= 0 ? 1.0 : -1.0;
        double cx = mx + sign * apothem * perpX;
        double cy = my + sign * apothem * perpY;

        double startAngle = Math.Atan2(st.Y - cy, st.X - cx);
        double endAngle = startAngle + theta;
        EmitArc(cx, cy, r, startAngle, endAngle, st, segs);
    }

    // -----------------------------------------------------------------
    // code7: サブシェイプ呼出
    // -----------------------------------------------------------------

    /// <summary>
    /// code7 のサブシェイプ番号のバイト数。unifont は 16bit の文字コードで 2 バイト。shapes 形式は 1 バイト
    /// （unifontの16bit文字コードと異なり0-255の範囲。公式のシェイプ定義規約でシェイプ番号がこの範囲に
    /// 制限されることに基づく。本機実物 ltypeshp.shx の6シェイプはこの命令を一度も使わず実測できていない
    /// 〔opcode7 の出現ゼロ。信頼度が低い前提であることを明記する〕）。ビッグフォントは先頭のバイトで形式が
    /// 変わるので null（呼び出し側が解釈する）。解釈と読み飛ばし（SkipOne）が同じ規則を使う。
    /// </summary>
    int? SubshapeCodeBytes() => _fontKind switch
    {
        ShxFontKind.Unifont => 2,
        ShxFontKind.Shapes => 1,
        _ => null,
    };

    int HandleSubshape(byte[] data, int i, PenState st, List<IrSegment> segs, int depth)
    {
        i++; // 7自体
        RequireBytes(data, i, 1, "code7(サブシェイプ呼出)");

        if (SubshapeCodeBytes() is int codeBytes)
        {
            RequireBytes(data, i, codeBytes, codeBytes == 2 ? "code7(サブシェイプ呼出、unifont 2バイト番号)" : "code7(サブシェイプ呼出)");
            int code = codeBytes == 2 ? (data[i] << 8) | data[i + 1] : data[i];
            i += codeBytes;
            byte[] sub = ResolveOrThrow(code);
            // 現在のペン位置・スケール・ペン状態を引き継いで再帰的に描画する。
            WalkInto(sub, st, segs, depth + 1);
            return i;
        }

        // BigFont
        int b0 = data[i];
        if (b0 == 0)
        {
            // 拡張形式: 7,0,primitive#(2,ビッグエンディアン),bx(1,符号付き),by(1,符号付き),
            // [w(1)](拡張のみ),h(1)
            i++;
            int need = _isExtended ? 6 : 5;
            RequireBytes(data, i, need, "code7,0(拡張ビッグフォント サブシェイプ配置)");
            int primitive = (data[i] << 8) | data[i + 1];
            i += 2;
            int bx = ToSByte(data[i]); i++;
            int by = ToSByte(data[i]); i++;
            double w, h;
            if (_isExtended) { w = data[i]; i++; h = data[i]; i++; }
            else { h = data[i]; i++; w = h; }

            byte[] sub = ResolveOrThrow(primitive);
            PlaceExtendedPrimitive(sub, bx * st.Scale, by * st.Scale, w * st.Scale, h * st.Scale,
                                    segs, depth);
            // 拡張プリミティブは basepoint を明示指定するため、現在のペン位置は動かさない
            // （複数プリミティブを順に絶対座標配置して1文字を組み立てる構成のため）。
            st.HasOpenSubpath = false;
            return i;
        }
        else
        {
            i++;
            byte[] sub = ResolveOrThrow(b0);
            WalkInto(sub, st, segs, depth + 1);
            return i;
        }
    }

    byte[] ResolveOrThrow(int code)
    {
        var b = _resolveGlyph(code);
        if (b == null)
            throw new InvalidDataException($"サブシェイプ code=0x{code:x4} が索引に見つからない");
        return b;
    }

    /// <summary>拡張ビッグフォントのプリミティブ配置。プリミティブ自身の幾何を独立した状態
    /// （スケール1・原点(0,0)）で描画し、そのバウンディングボックスを対象幅・高さへ非一様スケール
    /// してから basepoint(originX,originY) へ平行移動し、外側のセグメント列へ合成する。</summary>
    void PlaceExtendedPrimitive(byte[] primitiveBytecode, double originX, double originY,
                                double targetW, double targetH, List<IrSegment> outerSegs, int depth)
    {
        var localState = new PenState();
        var localSegs = new List<IrSegment>();
        WalkInto(primitiveBytecode, localState, localSegs, depth + 1);
        if (localSegs.Count == 0) return; // 描画要素のない空プリミティブ

        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;
        void Consider(double x, double y)
        {
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (y < minY) minY = y; if (y > maxY) maxY = y;
        }
        foreach (var s in localSegs)
        {
            switch (s)
            {
                case IrMove m: Consider(m.X, m.Y); break;
                case IrLine l: Consider(l.X, l.Y); break;
                case IrCubic c: Consider(c.X1, c.Y1); Consider(c.X2, c.Y2); Consider(c.X, c.Y); break;
            }
        }

        double bboxW = maxX - minX, bboxH = maxY - minY;
        double sx = bboxW > 1e-9 ? targetW / bboxW : 1.0;
        double sy = bboxH > 1e-9 ? targetH / bboxH : 1.0;

        // 描画範囲の最小点を原点へ正規化してからスケールし、基点へ平行移動する
        // （実測: extfont2.shx のプリミティブは原点基準の設計セルに描かれるが、
        //  描画範囲が原点から始まらないプリミティブもあるため、min の減算を省くと
        //  基点からずれて配置される）。
        double Tx(double x) => (x - minX) * sx + originX;
        double Ty(double y) => (y - minY) * sy + originY;
        foreach (var s in localSegs)
        {
            outerSegs.Add(s switch
            {
                IrMove m => new IrMove(Tx(m.X), Ty(m.Y)),
                IrLine l => new IrLine(Tx(l.X), Ty(l.Y)),
                IrCubic c => new IrCubic(Tx(c.X1), Ty(c.Y1), Tx(c.X2), Ty(c.Y2), Tx(c.X), Ty(c.Y)),
                _ => throw new NotSupportedException($"未知の IrSegment 型: {s.GetType()}"),
            });
        }
    }

    // -----------------------------------------------------------------
    // code14: 縦書き分岐の読み飛ばし
    // -----------------------------------------------------------------

    /// <summary>code14 の直後にある1命令ぶんだけ読み飛ばす（内容は解釈しない。水平レイアウト既定）。
    /// バイト数計算を誤ると以降の全バイト境界がずれるため、命令ごとの引数バイト数は
    /// メインループの解釈と同一の規則を用いる。</summary>
    int SkipOne(byte[] data, int i)
    {
        if (i >= data.Length) return i; // 末尾。読み飛ばす対象が無い
        int cb = data[i];
        if (cb is 0 or 1 or 2 or 5 or 6 or 14) return i + 1;
        if (cb is 3 or 4) return i + 2;
        if (cb == 7)
        {
            i++;
            if (SubshapeCodeBytes() is int codeBytes) return i + codeBytes;
            RequireBytes(data, i, 1, "code14直後のcode7スキップ");
            int subcode = data[i];
            if (subcode == 0)
            {
                i += 1 + 2 + 1 + 1; // 0バイト + primitive#(2) + bx(1) + by(1)
                i += _isExtended ? 2 : 1; // w+h または h のみ
                return i;
            }
            return i + 1;
        }
        if (cb == 8) return i + 3;
        if (cb == 9)
        {
            int j = i + 1;
            while (ReadDisplacement(data, ref j, "code14直後のcode9スキップ", out _, out _)) { }
            return j;
        }
        if (cb == 10) return i + 3;
        if (cb == 11) return i + 6;
        if (cb == 12) return i + 4;
        if (cb == 13)
        {
            int j = i + 1;
            while (ReadDisplacement(data, ref j, "code14直後のcode13スキップ", out _, out _)) j += 1; // 各組の後の bulge バイト
            return j;
        }
        return i + 1; // vector命令(16-255)
    }
}
