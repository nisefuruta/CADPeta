;; 複雑線種の記号（シェイプ・文字）検証用テストチャートを生成する。
;; 対象: 直線・ポリライン（Plinegen 両状態）・円・円弧、LTSCALE と個別線種尺度の変化、
;; ブロック内の記号線種を2ケース（挿入尺度1・挿入尺度2）含む。挿入尺度2は破線パターン・
;; 記号寸法・記号出現位置がブロック拡大率と二重にスケールされる欠陥の回帰ケース
;; （2026-07-10 是正。期待値: BATTING の記号半径≈10.16mm・ローカル100図面単位＝
;; 挿入尺度2で用紙上200mm相当の線上に記号9個。PDF印刷経路=accoreconsole プロットとの
;; 突き合わせで確定）。
(defun c:GENCHART ( / )
  (command "-linetype" "L" "GAS_LINE" "acadiso.lin" "")
  (command "-linetype" "L" "HOT_WATER_SUPPLY" "acadiso.lin" "")
  (command "-linetype" "L" "ZIGZAG" "acadiso.lin" "")
  (command "-linetype" "L" "TRACKS" "acadiso.lin" "")
  (command "-linetype" "L" "FENCELINE1" "acadiso.lin" "")
  (command "-linetype" "L" "FENCELINE2" "acadiso.lin" "")
  (command "-linetype" "L" "BATTING" "acadiso.lin" "")

  (setvar "LTSCALE" 2.0)
  (setvar "CELTSCALE" 1.0)
  (setvar "PLINEGEN" 0)

  ;; E1: LINE + GAS_LINE（文字記号）
  (command "-linetype" "S" "GAS_LINE" "")
  (command "_LINE" "0,0" "200,0" "")

  ;; E2: LWPOLYLINE（Plinegen オフ・既定）+ ZIGZAG（シェイプ記号、r=180 の第2要素含む）
  (setvar "PLINEGEN" 0)
  (command "-linetype" "S" "ZIGZAG" "")
  (command "_PLINE" "0,20" "80,20" "160,60" "240,20" "")

  ;; E3: LWPOLYLINE（Plinegen オン）+ TRACKS（シェイプ記号）。同じ折れ線形状で比較する
  (setvar "PLINEGEN" 1)
  (command "-linetype" "S" "TRACKS" "")
  (command "_PLINE" "0,50" "80,50" "160,90" "240,50" "")
  (setvar "PLINEGEN" 0)

  ;; E4: CIRCLE + FENCELINE1（シェイプ記号=円）
  (command "-linetype" "S" "FENCELINE1" "")
  (command "_CIRCLE" "300,50" "40")

  ;; E5: ARC + FENCELINE2（シェイプ記号=矩形）
  (command "-linetype" "S" "FENCELINE2" "")
  (command "_ARC" "400,20" "440,60" "480,20")

  ;; E6: LINE + HOT_WATER_SUPPLY（文字記号）。個別線種尺度2×LTSCALE2=合成4倍
  (setvar "CELTSCALE" 2.0)
  (command "-linetype" "S" "HOT_WATER_SUPPLY" "")
  (command "_LINE" "0,100" "200,100" "")
  (setvar "CELTSCALE" 1.0)

  ;; E7: ブロック内の記号線種（BATTING、シェイプ記号2個・r=0とr=180）。挿入尺度1（回帰基準）
  (command "-linetype" "S" "BATTING" "")
  (command "_LINE" "0,0" "100,0" "")
  (command "_BLOCK" "SYMBLK" "0,0" (entlast) "")
  (command "-linetype" "S" "BYLAYER" "")
  (command "_INSERT" "SYMBLK" "50,140" "1" "1" "30")

  ;; E8: 同じブロック SYMBLK を挿入尺度2（等方拡大、回転なし）で追加挿入する。破線パターン・
  ;; 記号寸法・記号出現位置がブロック拡大率で二重にスケールされる欠陥の回帰ケース
  (command "-linetype" "S" "BYLAYER" "")
  (command "_INSERT" "SYMBLK" "20,220" "2" "2" "0")

  (setvar "CELTSCALE" 1.0)
  (command "-linetype" "S" "BYLAYER" "")
  (princ)
)
(c:GENCHART)
