#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""extfont2.shx 等の個別グリフのバイトコード命令列を人が読める形で出力する調査スクリプト。
拡張ビッグフォントのサブシェイプ呼出（7,0）の実引数と、字送りを定義する構造
（送りプリミティブの有無・グリフ末尾の移動命令）を一次確認する。
使い方: uv run python dump_glyph_trace.py <shxパス> <グリフコード16進>...
"""
import sys
import shx_binary_survey as sv

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def trace(data: bytes, fc, indent="  "):
    """バイトコードを命令単位で1行ずつ出力する（解釈は survey と同一規則）。"""
    i = 0
    n = len(data)
    while i < n:
        cb = data[i]
        if cb > 14:
            ln = (cb >> 4) & 0xF
            d = cb & 0xF
            print(f"{indent}[{i:3d}] vec 0x{cb:02x} 方向{d} 長さ{ln}")
            i += 1
            continue
        if cb == 7:
            if fc.font_type == "bigfont":
                sub = data[i + 1]
                if sub == 0:
                    prim = (data[i + 2] << 8) | data[i + 3]
                    bx = sv.to_sbyte(data[i + 4])
                    by = sv.to_sbyte(data[i + 5])
                    if fc.is_extended:
                        w, h = data[i + 6], data[i + 7]
                        print(f"{indent}[{i:3d}] code7,0 prim=0x{prim:04x} base=({bx},{by}) w={w} h={h}")
                        i += 8
                    else:
                        h = data[i + 6]
                        print(f"{indent}[{i:3d}] code7,0 prim=0x{prim:04x} base=({bx},{by}) h={h}")
                        i += 7
                else:
                    print(f"{indent}[{i:3d}] code7 sub={sub}")
                    i += 2
            else:
                code = (data[i + 1] << 8) | data[i + 2]
                print(f"{indent}[{i:3d}] code7 unifont sub=0x{code:04x}")
                i += 3
            continue
        if cb in (0, 1, 2, 5, 6, 14):
            print(f"{indent}[{i:3d}] code{cb}")
            i += 1
            continue
        if cb in (3, 4):
            print(f"{indent}[{i:3d}] code{cb} 引数={data[i+1]}")
            i += 2
            continue
        if cb == 8:
            print(f"{indent}[{i:3d}] code8 dx={sv.to_sbyte(data[i+1])} dy={sv.to_sbyte(data[i+2])}")
            i += 3
            continue
        if cb == 9:
            j = i + 1
            pts = []
            while True:
                dx, dy = data[j], data[j + 1]
                j += 2
                if dx == 0 and dy == 0:
                    break
                pts.append((sv.to_sbyte(dx), sv.to_sbyte(dy)))
            print(f"{indent}[{i:3d}] code9 {pts}")
            i = j
            continue
        if cb == 10:
            print(f"{indent}[{i:3d}] code10 r={data[i+1]} flag=0x{data[i+2]:02x}")
            i += 3
            continue
        if cb == 12:
            print(f"{indent}[{i:3d}] code12 dx={sv.to_sbyte(data[i+1])} dy={sv.to_sbyte(data[i+2])} bulge={sv.to_sbyte(data[i+3])}")
            i += 4
            continue
        print(f"{indent}[{i:3d}] code{cb}（引数省略）")
        i += 1


def main():
    path = sys.argv[1]
    with open(path, "rb") as f:
        data = f.read()
    hdr = sv.parse_header(data)
    r = sv.Reader(data)
    r.pos = hdr.header_end_pos
    fc = sv.parse_bigfont(r) if hdr.font_type == "bigfont" else sv.parse_unifont(r)
    print(f"{path}: {hdr.font_type} above={fc.above} below={fc.below} extended={fc.is_extended}")

    for arg in sys.argv[2:]:
        code = int(arg, 16)
        bc = fc.glyphs.get(code)
        if bc is None:
            print(f"code 0x{code:04x}: 索引に無い")
            continue
        print(f"--- code 0x{code:04x} ({len(bc)} バイト): {bc.hex()}")
        trace(bc, fc)


if __name__ == "__main__":
    main()
