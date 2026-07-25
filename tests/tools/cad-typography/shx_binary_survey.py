#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SHX バイナリ形式の実地調査スクリプト（Phase 3 計画のための一次情報採取）。

目的: 本機に実在する AutoCAD 2026 の SHX フォントファイルを直接バイト列で読み、
以下を実測する。
  1. ヘッダ署名文字列（shapes / unifont / bigfont のいずれか）
  2. ヘッダ後続のインデックス構造（グリフ番号→バイト列オフセット/長さ）
  3. shape 0（フォント諸元: above/below/modes）の実値
  4. 全グリフを走査した命令コード（opcode 0-14）のヒストグラム
  5. 'A' や CP932 全角文字のデコード結果（ストローク座標列）
  6. ビッグフォント特有の「変更テーブル」（2バイト文字コード範囲指定）とサブシェイプ拡張形式

命令セットの意味（0-14）は AutoCAD シェイプ記述の公開仕様
（help.autodesk.com の "About Shape Descriptions"）に基づく。バイト列上の
具体的な配置・オフセット計算は本スクリプトが実ファイルを読んで検証する。

注意: 本スクリプトは調査専用であり、CADPeta の製品コード（src/）には一切関与しない。
"""

from __future__ import annotations

import struct
import sys
from collections import Counter
from dataclasses import dataclass, field

# Windows コンソールの既定コードページ(cp932)では日本語コメント由来の非ASCII文字や
# �(置換文字)を書き出せず落ちるため、標準出力を明示的にUTF-8へ切り替える。
sys.stdout.reconfigure(encoding="utf-8", errors="replace")


# ---------------------------------------------------------------------------
# 低レベル読み取りユーティリティ
# ---------------------------------------------------------------------------

class Reader:
    """バイト列を先頭から順に読み進めるだけの単純なカーソル。"""

    def __init__(self, data: bytes):
        self.data = data
        self.pos = 0

    def read(self, n: int) -> bytes:
        b = self.data[self.pos:self.pos + n]
        if len(b) != n:
            raise EOFError(f"要求 {n} バイトに対し {len(b)} バイトしか残っていない（位置 {self.pos}）")
        self.pos += n
        return b

    def u8(self) -> int:
        return self.read(1)[0]

    def u16(self) -> int:
        return struct.unpack_from("<H", self.read(2))[0]

    def i16(self) -> int:
        return struct.unpack_from("<h", self.read(2))[0]

    def u32(self) -> int:
        return struct.unpack_from("<I", self.read(4))[0]

    def i32(self) -> int:
        return struct.unpack_from("<i", self.read(4))[0]


def to_sbyte(v: int) -> int:
    """符号なしバイトを SHX 流の符号付きバイトへ変換する（(v&127)-(v&128 なら128)）。"""
    return (v & 127) - (128 if (v & 128) else 0)


# ---------------------------------------------------------------------------
# ヘッダ解析
# ---------------------------------------------------------------------------

@dataclass
class Header:
    raw_signature: str   # 例: "AutoCAD-86 bigfont 1.0"
    font_type: str       # "shapes" | "unifont" | "bigfont"
    version: str
    header_end_pos: int  # ヘッダ終端（0x1A の直後）の絶対バイト位置


def parse_header(data: bytes) -> Header:
    """
    ヘッダはテキストであり "\\r\\n\\x1a" で終端する。
    この終端シーケンスは AutoCAD の旧来のテキストファイル EOF 慣習（Ctrl-Z=0x1A）に由来。
    """
    pos = 0
    out = bytearray()
    n = len(data)
    while pos < n - 2:
        b1 = data[pos]
        pos += 1
        if b1 == 0x0D and data[pos] == 0x0A and data[pos + 1] == 0x1A:
            pos += 2
            break
        out.append(b1)
    sig = out.decode("ascii", errors="replace").strip()
    parts = sig.split(" ")
    font_type = parts[1].lower() if len(parts) > 1 else "?"
    version = parts[2] if len(parts) > 2 else "?"
    return Header(raw_signature=sig, font_type=font_type, version=version, header_end_pos=pos)


def split_name_and_bytecode(b: bytes):
    """シェイプ名（大文字ASCII）+ NUL + バイトコード の形。名前が無ければ先頭NULのみ。"""
    idx = b.find(0)
    if idx < 0:
        return None, b
    name = b[:idx].decode("ascii", errors="replace") if idx > 0 else None
    return name, b[idx + 1:]


# ---------------------------------------------------------------------------
# コンテンツ解析（形式ごと）
# ---------------------------------------------------------------------------

@dataclass
class GlyphIndexEntry:
    code: int
    length: int
    offset: int | None = None  # bigfont のみ絶対オフセットを持つ


@dataclass
class FontContent:
    font_type: str
    index_entries: list  # GlyphIndexEntry のリスト（読み取り順）
    glyphs: dict         # code -> bytecode (bytes)
    shape0_raw: bytes | None
    change_table: list = field(default_factory=list)  # bigfont: (lo,hi) のペア列
    is_extended: bool = False
    meta_text: str = ""
    above: int | None = None
    below: int | None = None
    modes: int | None = None


def parse_shapes(r: Reader) -> FontContent:
    """AutoCAD-86 shapes 1.0/1.1 形式（.shp を compile したシェイプファイル）。"""
    start_end_codes = r.read(4)  # 用途未確定の4バイト（開始/終了コード）。skip専用。
    count = r.i16()
    entries = []
    for _ in range(count):
        code = r.u16()
        length = r.u16()
        entries.append(GlyphIndexEntry(code=code, length=length))
    glyphs = {}
    shape0_raw = None
    for e in entries:
        if e.length <= 0:
            continue
        raw = r.read(e.length)
        if e.code == 0:
            shape0_raw = raw
            glyphs[0] = raw
        else:
            _, bytecode = split_name_and_bytecode(raw)
            glyphs[e.code] = bytecode
    fc = FontContent(font_type="shapes", index_entries=entries, glyphs=glyphs, shape0_raw=shape0_raw)
    _apply_shape0_metrics_simple(fc)
    return fc


def parse_unifont(r: Reader) -> FontContent:
    """AutoCAD-86 unifont 1.0 形式。"""
    count = r.i32()          # 総エントリ数（shape 0 を含む）
    info_len = r.i16()
    info = r.read(info_len)  # shape 0 の内容そのもの（ヘッダ側で直接読む）
    entries = []
    glyphs = {}
    for _ in range(count - 1):
        code = r.u16()
        length = r.u16()
        entries.append(GlyphIndexEntry(code=code, length=length))
        if length > 0:
            raw = r.read(length)
            _, bytecode = split_name_and_bytecode(raw)
            if len(bytecode) > 0:
                glyphs[code] = bytecode
    fc = FontContent(font_type="unifont", index_entries=entries, glyphs=glyphs, shape0_raw=info)
    _apply_shape0_metrics_simple(fc)
    return fc


def _apply_shape0_metrics_simple(fc: FontContent):
    """shapes/unifont 共通: shape0 = [ASCII文字列]\\0[above:1][below:1][modes:1] という並び。"""
    if not fc.shape0_raw:
        return
    raw = fc.shape0_raw
    term_idx = None
    for i, b in enumerate(raw):
        if b in (0x0D, 0x0A, 0x00):
            term_idx = i
            break
    if term_idx is None:
        return
    fc.meta_text = raw[:term_idx].decode("ascii", errors="replace")
    if term_idx + 3 < len(raw):
        fc.above = raw[term_idx + 1]
        fc.below = raw[term_idx + 2]
        fc.modes = raw[term_idx + 3]


def parse_bigfont(r: Reader) -> FontContent:
    """AutoCAD-86 bigfont 1.0 形式。"""
    item_length = r.i16()   # 用途: ヘッダ直後の予約フィールド（実測して報告する）
    count = r.i16()
    change_number = r.i16()

    change_table = []
    for _ in range(change_number):
        lo = r.u16()
        hi = r.u16()
        change_table.append((lo, hi))

    entries = []
    for _ in range(count):
        code = r.u16()
        length = r.u16()
        offset = r.u32()
        if code != 0 or length != 0 or offset != 0:
            entries.append(GlyphIndexEntry(code=code, length=length, offset=offset))

    glyphs = {}
    shape0_raw = None
    for e in entries:
        save = r.pos
        r.pos = e.offset
        raw = r.read(e.length)
        r.pos = save
        glyphs[e.code] = raw
        if e.code == 0:
            shape0_raw = raw

    fc = FontContent(
        font_type="bigfont",
        index_entries=entries,
        glyphs=glyphs,
        shape0_raw=shape0_raw,
        change_table=change_table,
    )
    fc.above = item_length  # 一時流用: 後で正しく上書き（下記参照）
    fc.above = None
    _apply_bigfont_metrics(fc)
    return fc


def _apply_bigfont_metrics(fc: FontContent):
    """
    bigfont の shape0 は UTF-8 風の説明文字列 + NUL 終端 + 数値メタ、という並びだが
    残りバイト数のパターンによって extended / non-extended が分かれる（実測で分岐を確認する）。
    """
    if not fc.shape0_raw:
        return
    raw = fc.shape0_raw
    # 説明文字列は 0x00 で終端。UTF-8想定だが本調査では手堅く ascii/latin1 で読み流す。
    nul = raw.find(0)
    if nul < 0:
        return
    fc.meta_text = raw[:nul].decode("latin1", errors="replace")
    tail = raw[nul + 1:]
    # 末尾の 0 パディングを剥がす
    while len(tail) > 0 and tail[-1] == 0:
        tail = tail[:-1]
    remaining = len(tail)
    if remaining >= 5:
        # extended: height, 0, modes, width, [0]
        fc.above = tail[0]
        fc.modes = tail[2]
        fc.below = 0
        fc.is_extended = True
    elif remaining == 4 and tail[1] == 0 and tail[3] > 0 and tail[3] != tail[0]:
        # 実測で判明した特殊分岐（extfont.shx 系列）: 残り4バイトでも
        # [height, 0, modes, width] という拡張形式と同じ並びになっている場合がある。
        # tail[1]==0 かつ tail[3]!=tail[0] かつ tail[3]>0 という条件でのみ拡張扱いとし、
        # これを見逃すと code7 サブシェイプ呼出の width/height 読み取りバイト数がずれ、
        # 後続グリフの解釈が全滅する（実測: extfont.shx で IndexError 多発として確認済み）。
        fc.above = tail[0]
        fc.modes = tail[2]
        fc.below = 0
        fc.is_extended = True
    elif remaining == 4:
        fc.above = tail[0]
        fc.below = tail[1]
        fc.modes = tail[2]
        fc.is_extended = False
    elif remaining == 3:
        fc.above = tail[0]
        fc.modes = tail[1]
        fc.below = 0
        # 実測: bigfont.shx (modes=2) はこの分岐に該当するが、is_extended=True と
        # 仮定すると code7 拡張形式の width/height 読み取りが2バイトになり、
        # 実データ3664グリフ全件を破綻なく解釈できない（IndexError多発）。
        # 1バイトのみ（非拡張）と仮定した場合は全件エラーなしで一貫して読み切れる。
        # したがって本調査ではこの分岐を非拡張として扱う（実測優先、要調査事項として記録）。
        fc.is_extended = False


# ---------------------------------------------------------------------------
# バイトコード命令ウォーカ（opcode ヒストグラム & ストローク列デコード共用）
# ---------------------------------------------------------------------------

VECTOR_DIR = {
    0: (1, 0), 1: (1, 0.5), 2: (1, 1), 3: (0.5, 1),
    4: (0, 1), 5: (-0.5, 1), 6: (-1, 1), 7: (-1, 0.5),
    8: (-1, 0), 9: (-1, -0.5), 10: (-1, -1), 11: (-0.5, -1),
    12: (0, -1), 13: (0.5, -1), 14: (1, -1), 15: (1, -0.5),
}


class Walker:
    """
    グリフのバイトコードを1回だけ順に解釈する。opcode 出現数を数えつつ、
    要求があればストローク座標列（ペンダウン区間ごとのポリライン）も組み立てる。
    命令の引数バイト数・終端条件は mlightcad/shx-parser（OSS実装、Autodesk公式の
    シェイプ記述コード0-14の説明と整合）を参照し、本スクリプトで独立に実装した。
    """

    def __init__(self, font_type: str, is_extended: bool, dual_orientation_skip: bool):
        self.font_type = font_type
        self.is_extended = is_extended
        # code14の直後命令をスキップするかどうか（水平レイアウト既定=スキップ）
        self.dual_orientation_skip = dual_orientation_skip
        self.hist = Counter()
        self.vector_count = 0

    def walk(self, data: bytes, collect_geometry: bool = False):
        i = 0
        n = len(data)
        pen_down = False
        cur = [0.0, 0.0]
        scale = 1.0
        stack = []
        polylines = []
        current_poly = []

        def flush():
            nonlocal current_poly
            if len(current_poly) > 1:
                polylines.append(current_poly)
            current_poly = []

        while i < n:
            cb = data[i]
            if cb <= 14:
                self.hist[cb] += 1
                if cb == 0:
                    # 実測で判明した点: code 0 はループを終端しない。
                    # グリフの境界は索引テーブルの length フィールドだけが決める
                    # （ペン状態のリセット/ポリライン確定の副作用のみを行い、続く
                    # バイト列があればそのまま解釈を継続する）。ビッグフォントの
                    # 複合グリフ（例: extfont.shx code=0x8379）は先頭付近に 0x00 が
                    # 出現しても後続に本体の描画命令が続くことを実データで確認した。
                    i += 1
                    flush()
                elif cb == 1:
                    if not pen_down and collect_geometry:
                        current_poly.append(tuple(cur))
                    pen_down = True
                    i += 1
                elif cb == 2:
                    pen_down = False
                    flush()
                    i += 1
                elif cb == 3:  # 除算
                    i += 1
                    denom = data[i]
                    scale /= denom
                    i += 1
                elif cb == 4:  # 乗算
                    i += 1
                    mul = data[i]
                    scale *= mul
                    i += 1
                elif cb == 5:  # push
                    stack.append(tuple(cur))
                    i += 1
                elif cb == 6:  # pop
                    if stack:
                        cur = list(stack.pop())
                    flush()
                    if pen_down and collect_geometry:
                        current_poly.append(tuple(cur))
                    i += 1
                elif cb == 7:  # サブシェイプ呼出
                    i += 1
                    if self.font_type == "shapes":
                        i += 1  # shape番号 1バイト
                    elif self.font_type == "unifont":
                        i += 2  # shape番号 2バイト(ビッグエンディアン的にbyte2つ)
                    elif self.font_type == "bigfont":
                        subcode = data[i]
                        if subcode == 0:
                            # 拡張形式: 7,0,primitive#(2),basepoint-x(1),basepoint-y(1),[width(1)],height(1)
                            i += 1  # 0 バイト自体
                            i += 2  # primitive# (2バイト)
                            i += 1  # basepoint-x
                            i += 1  # basepoint-y
                            i += 2 if self.is_extended else 1  # width+height または height のみ
                        else:
                            i += 1  # 通常のシェイプ番号 1バイト
                elif cb == 8:  # XY変位1組
                    i += 1
                    dx = to_sbyte(data[i]); i += 1
                    dy = to_sbyte(data[i]); i += 1
                    cur[0] += dx * scale
                    cur[1] += dy * scale
                    if pen_down and collect_geometry:
                        current_poly.append(tuple(cur))
                elif cb == 9:  # XY変位の並び、(0,0)で終端
                    i += 1
                    while True:
                        dx = to_sbyte(data[i])
                        dy = to_sbyte(data[i + 1])
                        i += 2
                        if dx == 0 and dy == 0:
                            break
                        cur[0] += dx * scale
                        cur[1] += dy * scale
                        if pen_down and collect_geometry:
                            current_poly.append(tuple(cur))
                elif cb == 10:  # 八分円弧: radius(1) + flag(1)
                    i += 1
                    i += 2
                elif cb == 11:  # フラクショナル弧: start(1)+end(1)+hr(1)+lr(1)+flag(1)
                    i += 1
                    i += 5
                elif cb == 12:  # バルジ弧1個: dx(1)+dy(1)+bulge(1)
                    i += 1
                    i += 3
                elif cb == 13:  # バルジ弧の並び、(dx,dy)=(0,0)で終端（終端に bulge バイトは無い）
                    i += 1
                    while True:
                        dx = data[i]
                        dy = data[i + 1]
                        i += 2
                        if dx == 0 and dy == 0:
                            break
                        i += 1  # bulge バイト
                elif cb == 14:  # 縦書き分岐: 引数バイトなし。次の1命令をスキップするか実行するかの分岐
                    i += 1
                    if self.dual_orientation_skip:
                        i = self._skip_one(data, i)
                else:
                    i += 1
            else:
                self.vector_count += 1
                length = (cb >> 4) & 0xF
                direction = cb & 0xF
                dx, dy = VECTOR_DIR[direction]
                cur[0] += dx * length * scale
                cur[1] += dy * length * scale
                if pen_down and collect_geometry:
                    current_poly.append(tuple(cur))
                i += 1

        flush()
        return polylines, tuple(cur)

    def _skip_one(self, data: bytes, i: int) -> int:
        """code14 の直後にある1命令分だけ読み飛ばす（内容は解釈しない）。"""
        if i >= len(data):
            return i
        cb = data[i]
        if cb == 0 or cb == 1 or cb == 2 or cb == 5 or cb == 6 or cb == 14:
            return i + 1
        if cb == 3 or cb == 4:
            return i + 2
        if cb == 7:
            i += 1
            if self.font_type == "shapes":
                return i + 1
            if self.font_type == "unifont":
                return i + 2
            if self.font_type == "bigfont":
                subcode = data[i]
                if subcode == 0:
                    i += 1 + 2 + 1 + 1
                    i += 2 if self.is_extended else 1
                    return i
                return i + 1
        if cb == 8:
            return i + 3
        if cb == 9:
            j = i + 1
            while True:
                dx = data[j]; dy = data[j + 1]; j += 2
                if dx == 0 and dy == 0:
                    return j
        if cb == 10:
            return i + 3
        if cb == 11:
            return i + 6
        if cb == 12:
            return i + 4
        if cb == 13:
            j = i + 1
            while True:
                dx = data[j]; dy = data[j + 1]; j += 2
                if dx == 0 and dy == 0:
                    return j
                j += 1
        return i + 1


# ---------------------------------------------------------------------------
# レポート出力
# ---------------------------------------------------------------------------

def hexdump(b: bytes, width: int = 16) -> str:
    lines = []
    for off in range(0, len(b), width):
        chunk = b[off:off + width]
        hexs = " ".join(f"{x:02x}" for x in chunk)
        asc = "".join(chr(x) if 32 <= x < 127 else "." for x in chunk)
        lines.append(f"  {off:04x}: {hexs:<{width*3}} {asc}")
    return "\n".join(lines)


def survey_file(path: str):
    print("=" * 100)
    print(f"ファイル: {path}")
    with open(path, "rb") as f:
        data = f.read()
    print(f"サイズ: {len(data)} バイト")

    hdr = parse_header(data)
    print(f"[1] 署名文字列（実測）: {hdr.raw_signature!r}")
    print(f"    形式判定: {hdr.font_type} / version={hdr.version}")
    print(f"    ヘッダ終端位置（コンテンツ開始オフセット）: 0x{hdr.header_end_pos:x} ({hdr.header_end_pos})")

    r = Reader(data)
    r.pos = hdr.header_end_pos

    content_start = r.pos
    if hdr.font_type == "shapes":
        fc = parse_shapes(r)
    elif hdr.font_type == "unifont":
        fc = parse_unifont(r)
    elif hdr.font_type == "bigfont":
        fc = parse_bigfont(r)
    else:
        print(f"    未知の形式のため解析中止: {hdr.font_type}")
        return None

    print(f"[2] グリフ数（有効エントリ）: {len(fc.glyphs)} 件 / インデックス総エントリ: {len(fc.index_entries)} 件")
    print(f"    索引テーブル先頭5件（読み取り順）:")
    for e in fc.index_entries[:5]:
        off_s = f" offset=0x{e.offset:x}" if e.offset is not None else ""
        print(f"      code=0x{e.code:04x} length={e.length}{off_s}")
    print(f"    索引テーブル末尾5件:")
    for e in fc.index_entries[-5:]:
        off_s = f" offset=0x{e.offset:x}" if e.offset is not None else ""
        print(f"      code=0x{e.code:04x} length={e.length}{off_s}")

    if fc.font_type == "bigfont":
        print(f"[2b] 変更テーブル（2バイト文字範囲指定, 実測 {len(fc.change_table)} 件）:")
        for lo, hi in fc.change_table[:20]:
            print(f"      range 0x{lo:04x} - 0x{hi:04x}")
        if len(fc.change_table) > 20:
            print(f"      ... 他 {len(fc.change_table) - 20} 件省略")

    print(f"[3] shape 0（フォント諸元）の生バイト列 ({len(fc.shape0_raw or b'')} バイト):")
    print(hexdump(fc.shape0_raw or b""))
    print(f"    説明文字列: {fc.meta_text!r}")
    print(f"    above(=キャップハイト基準?) = {fc.above}")
    print(f"    below = {fc.below}")
    print(f"    modes = {fc.modes}  (0=horizontal専用, 2=dual(既定horizontal), それ以外=vertical専用 という仮説を後段で検証)")
    print(f"    is_extended(ビッグフォント拡張形式) = {fc.is_extended}")

    # 縦書き分岐(code14)の扱い: modes==0 or 2 の場合は水平レイアウトが既定でスキップ、
    # bigfont は verticalDualMode でない限り常にスキップ、という参照実装の仮説をそのまま採用し
    # ヒストグラム走査でも同じ規約を適用する（水平レイアウト前提の実測）。
    dual_skip = True

    walker = Walker(font_type=fc.font_type, is_extended=fc.is_extended, dual_orientation_skip=dual_skip)
    decode_errors = []
    for code, bytecode in fc.glyphs.items():
        if code == 0:
            continue
        try:
            walker.walk(bytecode, collect_geometry=False)
        except Exception as ex:
            # 1グリフの解析失敗でファイル全体の調査を止めない。原因追跡のため
            # 例外の型・グリフコード・生バイト列を記録して後段で報告する。
            decode_errors.append((code, f"{type(ex).__name__}: {ex}", bytecode.hex()))

    print(f"[4] opcode 出現ヒストグラム（全 {len(fc.glyphs)} グリフ走査、shape0除く）:")
    for code in range(15):
        cnt = walker.hist.get(code, 0)
        if cnt:
            print(f"      code {code:2d}: {cnt}")
    print(f"      vector(16-255, 方向+長さ複合命令): {walker.vector_count}")
    if decode_errors:
        print(f"    走査中にエラーが発生したグリフ数: {len(decode_errors)} / {len(fc.glyphs)}")
        for code, msg, hex_bytes in decode_errors[:5]:
            print(f"      code=0x{code:04x}: {msg}")
            print(f"        生バイト列: {hex_bytes}")
    else:
        print(f"    走査エラーなし（全グリフ最後まで一貫してバイト境界を辿れた）")

    return fc, walker


def decode_and_print(fc, walker_ctx, code: int, label: str):
    if code not in fc.glyphs:
        print(f"    code 0x{code:x} ({label}) は本ファイルに存在しない")
        return
    bytecode = fc.glyphs[code]
    print(f"    code 0x{code:04x} ({label}) バイトコード({len(bytecode)}バイト): {bytecode.hex()}")
    w = Walker(font_type=fc.font_type, is_extended=fc.is_extended, dual_orientation_skip=True)
    try:
        polylines, last = w.walk(bytecode, collect_geometry=True)
    except Exception as ex:
        print(f"    デコード失敗: {type(ex).__name__}: {ex}")
        return
    print(f"    ペンダウン区間数: {len(polylines)}")
    for idx, poly in enumerate(polylines):
        pts = ", ".join(f"({x:.2f},{y:.2f})" for x, y in poly)
        print(f"      区間{idx}: {pts}")
    print(f"    終端カーソル位置: ({last[0]:.2f}, {last[1]:.2f})")


def main():
    files = [
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\romans.shx",
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\txt.shx",
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\extfont.shx",
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\bigfont.shx",
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\extfont2.shx",
        r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\@extfont2.shx",
    ]
    if len(sys.argv) > 1:
        files = sys.argv[1:]

    results = {}
    for path in files:
        try:
            res = survey_file(path)
            if res:
                results[path] = res
        except Exception as ex:
            print(f"!! {path} の解析に失敗: {ex!r}")

    # --- 'A' (romans.shx) と 全角文字 (extfont.shx) のデコード実演 ---
    print("\n" + "=" * 100)
    print("デコード実演")

    romans_path = r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\romans.shx"
    if romans_path in results:
        fc, w = results[romans_path]
        print(f"\n[romans.shx] 'A' (code 65 = 0x41) のデコード:")
        decode_and_print(fc, w, 65, "'A'")

    extfont_path = r"C:\Program Files\Autodesk\AutoCAD 2026\Fonts\extfont.shx"
    if extfont_path in results:
        fc, w = results[extfont_path]
        # CP932 の全角文字コードに該当しそうなグリフコードを探す（0x8140-0xFCFC 相当）
        candidates = [c for c in fc.glyphs.keys() if 0x8100 <= c <= 0xFCFC]
        print(f"\n[extfont.shx] CP932相当コード範囲のグリフ候補数: {len(candidates)}")
        sample = None
        # まず CJK統合漢字（U+4E00-U+9FFF）で実際に線が引かれる（ストローク数>0）グリフを探す。
        # 見つからなければ最初にデコードできた文字にフォールバックする。
        fallback = None
        for c in sorted(candidates):
            hi, lo = (c >> 8) & 0xFF, c & 0xFF
            try:
                ch = bytes([hi, lo]).decode("cp932")
            except UnicodeDecodeError:
                continue
            if fallback is None:
                fallback = (c, ch)
            if 0x4E00 <= ord(ch) <= 0x9FFF:
                w_probe = Walker(font_type=fc.font_type, is_extended=fc.is_extended, dual_orientation_skip=True)
                polylines, _ = w_probe.walk(fc.glyphs[c], collect_geometry=True)
                if polylines:
                    sample = (c, ch)
                    break
        if sample is None:
            sample = fallback
        if sample:
            code, ch = sample
            print(f"  採用コード: 0x{code:04x} → CP932デコード結果: {ch!r}")
            decode_and_print(fc, w, code, f"CP932 {ch!r}")
        else:
            print("  CP932として妥当にデコードできるコードが見つからなかった")

        # サブシェイプ呼出(code7)の実引数形式を実際のバイト列で確認
        print(f"\n[extfont.shx] code 7（サブシェイプ呼出）の引数形式サンプル:")
        shown = 0
        for c, bc in fc.glyphs.items():
            if c == 0:
                continue
            idx = bc.find(7)
            if idx >= 0 and idx + 1 < len(bc):
                subcode = bc[idx + 1]
                if subcode == 0 and idx + 7 < len(bc):
                    seg = bc[idx:idx + 8]
                    print(f"    code=0x{c:04x} 内 offset={idx}: {seg.hex()} "
                          f"→ 拡張形式(7,0,primitive#,bx,by,[w,]h) 候補")
                    shown += 1
                    if shown >= 5:
                        break


if __name__ == "__main__":
    main()
