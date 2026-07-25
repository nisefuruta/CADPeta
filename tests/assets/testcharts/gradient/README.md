# グラデーション正解チャート（計画 §2.2 の実測基盤）

本ディレクトリは、PETADATAのDB直接取得におけるグラデーションハッチのラスタ化に使う
**正解チャート一式と生成ハーネス**である。現行仕様は `docs/02_設計書.md`、
検証結果は `docs/validation/gradient-rasterization.md` を基準とする。
headless（accoreconsole）で全9形状の正解チャートを再現生成できることを実機確認済み（2026-07-13、
AutoCAD 2026 console）。

## charts/ の内容（29枚）

| 種別 | 命名 | 枚数 |
|---|---|---|
| 黒→白・角度0・Shift0 | `grad_<形状>_bw_a000_s000.pdf` | 9 |
| 黒→白・角度45°・Shift0 | `grad_<形状>_bw_a045_s000.pdf` | 9 |
| 黒→白・角度0・Shift0.5 | `grad_<形状>_bw_a000_s050.pdf` | 9 |
| 色検証（赤→青） | `grad_{LINEAR,SPHERICAL}_color_a000_s000.pdf` | 2 |

形状は GFNAME 公式9種: LINEAR / CYLINDER / INVCYLINDER / SPHERICAL / INVSPHERICAL /
HEMISPHERICAL / INVHEMISPHERICAL / CURVED / INVCURVED（LINEAR に INV は無い）。

- ハッチ境界: 矩形 (40,40)–(380,260) mm（モデル空間、A3 フルブリード 420×297 の窓 0,0–420,297 を 1:1 プロット）
- 2色 TwoColor（452=0）。OneColor チャートは作らない（取得アダプタは API が返す解決済み
  ストップを使う規約のため、OneColor の色解決は API 側で行われる。計画 §4.3）
- `charts.log` = 生成時の entget 証跡（450/452/453/460/461/470）。全29枚 entmake=OK を確認済み

## 使い方（t(x,y) の導出）

AutoCAD はプロット時にグラデーションハッチを **JPEG＋SMask** へ
ラスタ化して PDF に埋める（Shading は使わない。handoff §3b）。したがって:

1. `cadpeta.exe <pdf> out.svg --png out.png --png-dpi 200` でPDF印刷経由の合成 PNG を得る
   （または PdfPig で画像+SMask を直接取り出す）。
2. 黒→白チャートでは **画素の輝度がそのまま t(x,y)×255**。JPEG 由来のノイズは±2程度
   （滑らかな解析形のフィット＋中央値フィルタで吸収する）。
3. 導出した式は色チャート（赤→青）と Shift/角度バリエーションで交差検証する。

注意:
- 境界近傍（目安 2〜3mm）には AutoCAD 自身のレンダ由来の縁アーチファクト（三角分割の継ぎ目・
  ギザ）が乗る。フィットは境界を除いた内部領域で行うこと（許容差の規定は計画 §2.2）。
- 本チャートは accoreconsole 経路。2026-07-14 に `LINEAR/a000/s000` を AutoCAD 2026 の
  フル GUI（`WinSta0\Default`、COM `PlotToFile`）でも同条件で併測した。両PDFは
  `/DCTDecode=1`、`/SMask=1`、画像 `6426×4158px`、配置 `340.0108×220.0063mm`
  （両軸480dpi）で一致した。同じCLIで正規化した SVG・diag.json・埋込PNGはそれぞれ
  SHA-256完全一致し、全画素差0。対話/console差がないことを確認済み。

## 生成ハーネス

- `gh_charts.lsp` — entmake でグラデーションハッチを1枚生成し entget 証跡を charts.log へ追記
- `gen_run.ps1` — 29枚を一括生成（1チャート=1 accoreconsole プロセス・タイムアウト kill・
  FileDialog レジストリ復元まで自動）。パラメータで accoreconsole パスと dwt を差し替え可能
- `probe63.ps1` — 下記の落とし穴を特定した決定実験（3枚）。再実行可能な証拠として残す
- `golden_samples.json` — 黒白27枚の境界3mm除外・17×11固定格子輝度、元PDF名とSHA-256、
  許容統計。`CadPeta.Tests` へ埋め込み、全9形状・INV・角度・Shift・係数転記をCIで検査する

## E2Eフィクスチャ図面（gradient_e2e.dwg）

`gradient_e2e.dwg` は、実機E2E（`tests/tools/pdf-emf/gradient_compat_e2e.ps1` の `-SourceDwg` 入力、
AutoCAD単体は `gradient_e2e_autocad.ps1`）が開く9形状フィクスチャのコミット済みスナップショットである。
`tests/tools/pdf-emf/gradient_e2e.lsp` の `GRADIENTE2E` が、公式9形状の赤→青 TwoColor ハッチ
（463=0.0/421=16711680・463=1.0/421=255、落とし穴1のとおり 63 グループなし）を
3列×3行のタイル（各 55×35mm、ピッチ 70/50mm）として entmake する。角度は偶数番が0・奇数番が45°、
最下段の3種（INVHEMISPHERICAL・CURVED・INVCURVED）は Shift=1.0。AutoCAD 2026 accoreconsole で生成し 2018 形式で保存した
（2026-07-16、同日の PETADATA 実行で9グラデーション9タイル取得を確認済み）。
SHA-256: `b2094d43706c051ebc5f1b2ce8e46238d2709533152357e138b97b0d7e0ce867`。
同一内容の図面は `gradient_e2e_autocad.ps1` でいつでも再生成できる（DWGバイナリは生成日時等で変わる）。

解析器と対話併測ハーネス:

- `tests/tools/pdf-emf/gradient_fit` — `production`（全面統計）、`golden`（固定格子JSON再生成）、
  `pnghash`（net48/net8/net10共通PNGハッシュ再生成）
- `tests/tools/pdf-emf/gradient_interactive_plot.ps1` — AutoCAD 2026フルGUIの1ケース採取。
  非対話デスクトップでは起動前に拒否し、STA COM message filter、quiescent待機、所有PID限定の
  後始末、FileDialog復元を行う。Codex環境からは対話ログオン型Scheduled Taskで実行する

## 落とし穴（実測で確定）

1. **グラデーション色ストップに ACI 7 を付けてはならない**。63=7（白/黒の文脈依存色）が両ストップに
   付くと、GFNAME・421 真色に関係なく**全体が単色で描画される**（29枚全滅の実例）。通常の ACI
   （63=1 等）なら 421 真色が勝つ。ハーネスは 63 グループ自体を省略する（421 のみ）。純黒 421=0 も
   63 を省略すれば正しく描画される。
2. accoreconsole はプロット完了後に残留・ハングすることがある（PDF は完成済みでもプロセスが
   終わらない）。1チャート=1プロセス＋PDF 完成検知＋タイムアウト kill で回避し、kill 後は
   FileDialog=0 のレジストリ永続化を必ず復元する（gen_run.ps1 が自動で行う）。
3. entmake のグラデーション HATCH は境界をエッジ形式（92=1・93=4・72=1×4）で受け付ける。
   DXF 主要コード: 450=1・451=0・460=角度rad・461=Shift・452=0(2色)・462・453=2・
   463/421 ペア×2・470=形状名。

## 位置づけ

本ディレクトリは §2.2 実測の一次資料・再生成可能な検証資産の正式な置き場である。
一時 `.scr` / console出力は `tmp/` に隔離し、正解PDF・entget証跡・生成器・ゴールデンだけを追跡する。
