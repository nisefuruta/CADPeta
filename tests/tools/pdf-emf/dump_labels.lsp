;; TESTCHART2.dwg の「-」プレースホルダTEXTの色・透過性を実測ダンプする
;; （PDF側でピンク描画される件のDWG側正解データ取得。出力は ASCII のみ）
(defun c:DUMPLABELS ( / ss i e d f n)
  (setq f (open "C:/Users/wk/repo/CADPeta/tests/tools/pdf-emf/labels_dump.txt" "w"))
  (setq ss (ssget "_X" '((0 . "TEXT") (1 . "-"))))
  (if ss
    (progn
      (write-line (strcat "count=" (itoa (sslength ss))) f)
      (setq i 0 n (sslength ss))
      (while (< i n)
        (setq e (ssname ss i) d (entget e '("*")))
        (write-line
          (strcat (itoa i)
                  " pos=" (vl-prin1-to-string (cdr (assoc 10 d)))
                  " 62=" (if (assoc 62 d) (itoa (cdr (assoc 62 d))) "ByLayer")
                  " 440=" (if (assoc 440 d) (itoa (cdr (assoc 440 d))) "ByLayer"))
          f)
        (setq i (1+ i)))))
  ;; レイヤ CHART_LABEL の定義も出す
  (setq d (tblsearch "LAYER" "CHART_LABEL"))
  (write-line (strcat "layer CHART_LABEL: " (vl-prin1-to-string d)) f)
  (close f)
  (princ "DUMPLABELS done")
  (princ))
