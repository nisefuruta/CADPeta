# 検証根拠

ここには現行設計を支える、再現可能で最終的な検証結果だけを置く。開発途中の仮説、試行錯誤、
生成画像や差分タイルは置かない。

| 文書 | 対象 |
|---|---|
| `architecture-decisions.md` | PDF、IR、EMF、PETA貼付／PETADATA書き出し、文字、互換CADに関する採用判断 |
| `command-option-matrix.md` | PETA・PETADATAのオプション全因子マトリクスと三点検証の結果 |
| `gradient-rasterization.md` | 全9形状の式、AutoCAD対話/console、4製品API/E2E、Office、CLI背景色 |
| `compatible-cad.md` | ARES・IJCAD・BricsCADのPDF印刷経路と製品差 |
| `distribution.md` | 配布ZIPの構造、依存関係、導入経路 |
| `release-gates-2026-07-19.md` | 全CAD実機、ページ設定pairwise、Office／クリップボード競合、配布ZIP、sysvar lint（残関門は実鍵署名のみ） |

入力コーパスは `tests/assets/`、再実行ハーネスは `tests/tools/` にある。

