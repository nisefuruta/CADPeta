;;; probe_stale10.lsp
;;; 2つの裏取りを1回の console 実行で行う。
;;; (1) フォント解決: accoreconsole が msgothic.ttc / arial.ttf を findfile で
;;;     解決できるか（行19 のPDF印刷経路「?」化の原因切り分け）
;;; (2) group 10 の堅牢性: entmake で中央揃え(72=1)の TEXT を「group 10 を故意に
;;;     でたらめ(0,0)・group 11 を正」で作った場合、AutoCAD が group 10 を
;;;     再計算するか。三次製品や手書き DXF 由来の図面で Position 直使用が
;;;     破綻し得るかの証拠にする。
;;; 使い方: (load "...probe_stale10.lsp") の後 (c:PROBESTALE10 "ダンプ出力先.txt")
(defun c:PROBESTALE10 (dumppath / f e ename)
  (setq f (open dumppath "w"))

  ;; ---- (1) フォント解決 ----
  (write-line (strcat "findfile msgothic.ttc = "
                      (cond ((findfile "msgothic.ttc")) (T "nil"))) f)
  (write-line (strcat "findfile arial.ttf    = "
                      (cond ((findfile "arial.ttf")) (T "nil"))) f)
  (write-line (strcat "findfile txt.shx      = "
                      (cond ((findfile "txt.shx")) (T "nil"))) f)

  ;; ---- (2) 偽 group 10 の中央揃え TEXT ----
  (entmake (list '(0 . "TEXT") '(8 . "0") '(62 . 7)
                 '(10 0.0 0.0 0.0)          ; 故意のでたらめ（本来は再計算値が入る位置）
                 '(40 . 8.0) '(1 . "StaleTest") '(50 . 0.0) '(41 . 1.0)
                 '(7 . "Standard")
                 '(72 . 1)                   ; Center
                 '(11 100.0 50.0 0.0)        ; 位置合わせ点（正）
                 '(73 . 0)))
  (setq ename (entlast))
  (setq e (entget ename))
  (write-line "-- entmake直後 (72=1 Center, 10=(0,0)故意, 11=(100,50)) --" f)
  (write-line (vl-prin1-to-string e) f)

  (command "_.REGEN")
  (setq e (entget ename))
  (write-line "-- REGEN後 --" f)
  (write-line (vl-prin1-to-string e) f)

  ;; エンティティを開いて閉じ直す操作（entmod の no-op）でも再計算されるか
  (entmod (entget ename))
  (setq e (entget ename))
  (write-line "-- entmod(no-op)後 --" f)
  (write-line (vl-prin1-to-string e) f)

  (close f)
  (princ (strcat "\nPROBESTALE10: " dumppath " へ書き出した。"))
  (princ))
(princ "\nprobe_stale10.lsp をロードしました。")
(princ)
