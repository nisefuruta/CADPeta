;; 記号が長さ0のダッシュ要素に乗る、偽ドット検証専用のケース。
(defun c:GENZERO ( / )
  (command "-linetype" "L" "TESTSHAPE_ZERO"
           "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\test_symbols.lin" "")
  (setvar "LTSCALE" 1.0)
  (setvar "CELTSCALE" 1.0)
  (command "-linetype" "S" "TESTSHAPE_ZERO" "")
  (command "_LINE" "0,0" "100,0" "")
  (princ)
)
(c:GENZERO)
