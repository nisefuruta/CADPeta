;; R=/A=/U= 回転指定と group 74 の対応を実測するプローブ。
(defun c:PROBE2 ( / f names name ltid elist)
  (setq f (open "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\test_symbols_dump.txt" "w"))
  (setq names (list "TESTSHAPE_BASE" "TESTSHAPE_R" "TESTSHAPE_A" "TESTSHAPE_U"
                    "TESTTEXT_BASE" "TESTTEXT_R" "TESTTEXT_A" "TESTTEXT_U" "TESTSHAPE_XY"))
  (foreach name names
    (progn
      (command "-linetype" "L" name "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\test_symbols.lin" "")
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
(c:PROBE2)
