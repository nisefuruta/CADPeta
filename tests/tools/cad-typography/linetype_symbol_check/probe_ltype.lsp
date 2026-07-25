;; 複雑線種の記号情報を entget で実測するプローブ。
;; acadiso.lin から記号入り線種を読み込み、LTYPE シンボルテーブルの生 DXF を書き出す。
(defun c:PROBE ( / f names name ltid elist)
  (setq f (open "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\ltype_dump.txt" "w"))
  (setq names (list "GAS_LINE" "HOT_WATER_SUPPLY" "FENCELINE1" "FENCELINE2" "TRACKS" "BATTING" "ZIGZAG"))
  (foreach name names
    (progn
      (command "-linetype" "L" name "acadiso.lin" "")
      (setq ltid (tblobjname "LTYPE" name))
      (if ltid
        (progn
          (setq elist (entget ltid))
          (write-line (strcat "=== " name " ===") f)
          (foreach pair elist
            (write-line (vl-princ-to-string pair) f))
          (write-line "" f))
        (write-line (strcat "=== " name " (見つからず) ===") f)
      )
    )
  )
  (close f)
  (princ)
)
(c:PROBE)
