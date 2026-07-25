;; 端部整列 受け入れ検証: 記号線種を含むブロックを尺度2で挿入するケース（未検証点の解消）。
;; gen_test_chart.lsp の SYMBLK と同型（BATTING、シェイプ記号2個）だが、
;; INSERT の尺度を2（等方）にして記号↔破線の整合が崩れないかを見る。
(defun c:GENBLKSCALE2 ( / )
  (command "-linetype" "L" "BATTING" "acadiso.lin" "")
  (setvar "LTSCALE" 2.0)
  (setvar "CELTSCALE" 1.0)
  (setvar "PLINEGEN" 0)

  (command "-linetype" "S" "BATTING" "")
  (command "_LINE" "0,0" "100,0" "")
  (command "_BLOCK" "SYMBLK2" "0,0" (entlast) "")
  (command "-linetype" "S" "BYLAYER" "")
  ;; 尺度2（等方）・回転なしで挿入。基点(20,20)
  (command "_INSERT" "SYMBLK2" "20,20" "2" "2" "0")

  (setvar "CELTSCALE" 1.0)
  (command "-linetype" "S" "BYLAYER" "")
  (princ)
)
(c:GENBLKSCALE2)
