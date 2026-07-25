;; シェイプフォント不在エラー経路の検証: 正常にロードした線種のシェイプスタイル
;; （群340が指すTEXTSTYLEレコード）のファイル名だけを entmod で実在しないSHXへ書き換える。
(defun c:GENMISSINGFONT3 ( / ltid elist dashpair styleid selist newlist f)
  (command "-linetype" "L" "TESTSHAPE_ZERO"
           "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\test_symbols.lin" "")
  (setq ltid (tblobjname "LTYPE" "TESTSHAPE_ZERO"))
  (setq elist (entget ltid))
  (setq dashpair (assoc 340 elist))
  (setq styleid (cdr dashpair))
  (setq selist (entget styleid))
  (setq newlist
    (mapcar '(lambda (pair) (if (= (car pair) 3) (cons 3 "cadpeta_missing_shape.shx") pair))
            selist))
  (entmod newlist)
  (setq f (open "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltsym\\shapefont_verify.txt" "w"))
  (foreach pair (entget styleid) (write-line (vl-princ-to-string pair) f))
  (close f)
  (setvar "LTSCALE" 1.0)
  (command "-linetype" "S" "TESTSHAPE_ZERO" "")
  (command "_LINE" "0,0" "100,0" "")
  (command "-linetype" "S" "BYLAYER" "")
  (princ)
)
(c:GENMISSINGFONT3)
