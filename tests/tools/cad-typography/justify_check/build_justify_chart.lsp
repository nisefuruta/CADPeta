;;; ============================================================
;;; build_justify_chart.lsp
;;; 構造SVG書き出し（AutoCAD DB直接取得）の文字写像が DBText.Position（DXF group 10）だけを
;;; 使っている現状で、左揃え以外の Justify（中央・右・整列・フィット・TL~BR等）で
;;; 位置ずれが出ないかを実測するためのテストチャート図面を作る。
;;;
;;; 各行は実際の TEXT コマンド（(command "_.TEXT" ...)）で作成する（entmake で
;;; DXF group を直接でっち上げると、実際のユーザー図面と異なる可能性があるため、
;;; AutoCAD 自身に配置させて DXF group を書かせる）。各行の「真のアンカー点」
;;; （コマンドへ渡した点）に赤十字マーカーを引き、entget で group 10/11/40/41/50/72/73
;;; を実測してダンプする。
;;;
;;; 使い方: (load "...build_justify_chart.lsp") の後 (c:BUILDJUSTIFYCHART "TSV出力先" "ダンプ出力先")
;;; ============================================================

;; ---- 見当枠（左下・右上、mm。tests/tools/cad-typography/glyph_render_check と同一定数を流用） ----
(setq jc:frame-x0 10.0 jc:frame-y0 10.0
      jc:frame-x1 410.0 jc:frame-y1 287.0)

;; ---- 行レイアウト定数 ----
;; anchor-x=80: 右寄せ系（Right/TR/MR/BR）は文字列がアンカーから左へ約55mm 伸びるため、
;; プロット窓の左端（CAD x=10）で切れない位置に置く（60 では x=4.7 まで達し B 側が
;; クリップされる実測があった）
;; row0-y=272: 1行目の文字キャップ（ベースライン+8mm+オーバーシュート）がプロット窓の
;; 上端（CAD y=287）を越えない位置に置く（280 では B 側で頭が切れる実測があった）
(setq jc:anchor-x 80.0      ; 単点系Justifyのアンカー点X
      jc:align-dx 100.0     ; Align/Fit の2点間距離(mm)
      jc:row0-y 272.0       ; 1行目のY
      jc:row-h  14.0        ; 行ピッチ
      jc:text-h 8.0)        ; 文字高さ（mm）

;; ---- 回転ケースの配置（本体行の右側の空き領域 x>=230 を使う） ----
(setq jc:rot-x 260.0)

(setq jc:fonts-dir "C:/Program Files/Autodesk/AutoCAD 2026/Fonts/")
(setq jc:str-tt   "AaGgJj149")             ; TrueType行の共通文字列
(setq jc:str-shx  "亜寸法検証123")          ; SHX(ビッグフォント)行の文字列
(setq jc:str-jp   "日本語ｱｲｳ123")           ; 日本語TrueType行の文字列

;; ---- スタイル作成 ----
(defun jc:mkstyle-shx (name font big)
  (if (not (tblsearch "STYLE" name))
    (entmake (list '(0 . "STYLE") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbTextStyleTableRecord")
                   (cons 2 name) (cons 3 font) (cons 4 (if big big ""))
                   '(70 . 0) '(40 . 0.0) '(41 . 1.0) '(50 . 0.0)
                   '(71 . 0) '(42 . 2.5)))))

;; TrueType はフォントファイル名の entmake で作る（tests/assets/testcharts/gen_testchart.lsp の
;; 実績方式。-STYLE コマンドは accoreconsole で "MS Gothic" のフォント名対話が崩れ
;; ハングした実測があるため使わない。group 3 へ .ttf/.ttc ファイル名を書けば
;; AutoCAD がフォントを解決する）
(defun jc:mkstyle-tt (name fontfile)
  (if (not (tblsearch "STYLE" name))
    (entmake (list '(0 . "STYLE") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbTextStyleTableRecord")
                   (cons 2 name) (cons 3 fontfile) '(4 . "")
                   '(70 . 0) '(40 . 0.0) '(41 . 1.0) '(50 . 0.0)
                   '(71 . 0) '(42 . 2.5)))))

;; ---- アンカーの十字マーカー（真のアンカー点。ACI1=赤、細線） ----
(defun jc:mark (pt / x y)
  (setq x (car pt) y (cadr pt))
  (entmake (list '(0 . "LINE") '(8 . "0") '(62 . 1)
                 (list 10 (- x 2.0) y 0.0) (list 11 (+ x 2.0) y 0.0)))
  (entmake (list '(0 . "LINE") '(8 . "0") '(62 . 1)
                 (list 10 x (- y 2.0) 0.0) (list 11 x (+ y 2.0) 0.0))))

;; ---- 左端の行番号ラベル（測定領域(x>=30)の外、目視識別用。ACI3=緑） ----
(defun jc:label (rowidx y str)
  (entmake (list '(0 . "TEXT") '(8 . "0") '(62 . 3)
                 (list 10 12.0 (- y 1.0) 0.0) (cons 40 3.0)
                 (cons 1 (strcat (itoa rowidx) ":" str))
                 '(50 . 0.0) '(41 . 1.0) '(7 . "Standard") '(72 . 0) '(73 . 0)
                 (list 11 0.0 0.0 0.0))))

(defun jc:mkframe ()
  (entmake (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") '(100 . "AcDbPolyline")
                 (cons 90 4) (cons 70 1) '(62 . 7)
                 (list 10 jc:frame-x0 jc:frame-y0)
                 (list 10 jc:frame-x1 jc:frame-y0)
                 (list 10 jc:frame-x1 jc:frame-y1)
                 (list 10 jc:frame-x0 jc:frame-y1))))

;; ---- 単点系 Justify（既定[nil]/C/R/M/TL/TC/TR/ML/MC/MR/BL/BC/BR） ----
(defun jc:mk-simple (keyword pt height rot str / e)
  (if keyword
    (command "_.TEXT" "_J" keyword pt height rot str "")
    (command "_.TEXT" pt height rot str ""))
  (entlast))

;; ---- Align（2点、高さ・回転は導出） ----
(defun jc:mk-align (pt1 pt2 str)
  (command "_.TEXT" "_J" "_A" pt1 pt2 str "")
  (entlast))

;; ---- Fit（2点＋高さ指定、幅係数は導出） ----
(defun jc:mk-fit (pt1 pt2 height str)
  (command "_.TEXT" "_J" "_F" pt1 pt2 height str "")
  (entlast))

;; ---- entget の該当グループを TSV 1行へ整形して書く ----
(defun jc:dump-row (f rowidx typename str ename stylename isshx
                     / e p10 p11 h wf rot h72 h73)
  (setq e (entget ename))
  (setq p10 (cdr (assoc 10 e)))
  (setq p11 (cdr (assoc 11 e)))
  (setq h (cdr (assoc 40 e)))
  (setq wf (cond ((assoc 41 e) (cdr (assoc 41 e))) (T 1.0)))
  (setq rot (cond ((assoc 50 e) (cdr (assoc 50 e))) (T 0.0)))
  (setq h72 (cond ((assoc 72 e) (cdr (assoc 72 e))) (T 0)))
  (setq h73 (cond ((assoc 73 e) (cdr (assoc 73 e))) (T 0)))
  (write-line
    (strcat (itoa rowidx) "\t" typename "\t" str "\t" stylename "\t"
             (if isshx "1" "0") "\t"
             (rtos (car p10) 2 6) "\t" (rtos (cadr p10) 2 6) "\t"
             (rtos (car p11) 2 6) "\t" (rtos (cadr p11) 2 6) "\t"
             (rtos h 2 6) "\t" (rtos wf 2 6) "\t" (rtos rot 2 6) "\t"
             (itoa (fix h72)) "\t" (itoa (fix h73)))
    f))

;; ---- アンカーTSV（真のアンカー点。row_bbox の測定にも使う） ----
(defun jc:anchor-row (fa rowidx typename ptx1 pty1 ptx2 pty2 rowy)
  (write-line
    (strcat (itoa rowidx) "\t" typename "\t"
             (rtos ptx1 2 6) "\t" (rtos pty1 2 6) "\t"
             (if ptx2 (rtos ptx2 2 6) "") "\t"
             (if pty2 (rtos pty2 2 6) "") "\t"
             (rtos rowy 2 6))
    fa))

(defun c:BUILDJUSTIFYCHART (tsvpath dumppath / f fd y rowidx pt pt2 ename)
  (jc:mkstyle-shx "txt_ext2" (strcat jc:fonts-dir "txt.shx") (strcat jc:fonts-dir "extfont2.shx"))
  (jc:mkstyle-tt "arial_tt" "arial.ttf")
  (jc:mkstyle-tt "msgothic_tt" "msgothic.ttc")
  (jc:mkframe)

  (setq f (open tsvpath "w"))
  (write-line "row\ttype\tanchorX1\tanchorY1\tanchorX2\tanchorY2\trowY" f)
  (setq fd (open dumppath "w"))
  (write-line "row\ttype\tstr\tstyle\tisShx\tg10x\tg10y\tg11x\tg11y\tg40h\tg41wf\tg50rot\tg72\tg73" fd)

  (setvar "TEXTSTYLE" "arial_tt")
  (setq rowidx 0 y jc:row0-y)

  ;; ---- 1: 既定(左揃え、J無指定) ----
  (setq rowidx (1+ rowidx))
  (setq pt (list jc:anchor-x y 0.0))
  (setq ename (jc:mk-simple nil pt jc:text-h 0.0 jc:str-tt))
  (jc:mark pt) (jc:label rowidx y "Left(既定)")
  (jc:anchor-row f rowidx "Left" (car pt) (cadr pt) nil nil y)
  (jc:dump-row fd rowidx "Left" jc:str-tt ename "arial_tt" nil)
  (setq y (- y jc:row-h))

  ;; ---- 2~4: Center/Right/Middle(単点、高さ・回転あり) ----
  (foreach kw2 (list (list "_C" "Center") (list "_R" "Right") (list "_M" "Middle"))
    (setq rowidx (1+ rowidx))
    (setq pt (list jc:anchor-x y 0.0))
    (setq ename (jc:mk-simple (car kw2) pt jc:text-h 0.0 jc:str-tt))
    (jc:mark pt) (jc:label rowidx y (cadr kw2))
    (jc:anchor-row f rowidx (cadr kw2) (car pt) (cadr pt) nil nil y)
    (jc:dump-row fd rowidx (cadr kw2) jc:str-tt ename "arial_tt" nil)
    (setq y (- y jc:row-h)))

  ;; ---- 5: Align(2点、高さ・回転は導出) ----
  (setq rowidx (1+ rowidx))
  (setq pt (list jc:anchor-x y 0.0) pt2 (list (+ jc:anchor-x jc:align-dx) y 0.0))
  (setq ename (jc:mk-align pt pt2 jc:str-tt))
  (jc:mark pt) (jc:mark pt2) (jc:label rowidx y "Align")
  (jc:anchor-row f rowidx "Align" (car pt) (cadr pt) (car pt2) (cadr pt2) y)
  (jc:dump-row fd rowidx "Align" jc:str-tt ename "arial_tt" nil)
  (setq y (- y jc:row-h))

  ;; ---- 6: Fit(2点＋高さ指定、幅係数は導出) ----
  (setq rowidx (1+ rowidx))
  (setq pt (list jc:anchor-x y 0.0) pt2 (list (+ jc:anchor-x jc:align-dx) y 0.0))
  (setq ename (jc:mk-fit pt pt2 jc:text-h jc:str-tt))
  (jc:mark pt) (jc:mark pt2) (jc:label rowidx y "Fit")
  (jc:anchor-row f rowidx "Fit" (car pt) (cadr pt) (car pt2) (cadr pt2) y)
  (jc:dump-row fd rowidx "Fit" jc:str-tt ename "arial_tt" nil)
  (setq y (- y jc:row-h))

  ;; ---- 7~15: TL/TC/TR/ML/MC/MR/BL/BC/BR(単点、高さ・回転あり) ----
  (foreach kw3 (list (list "_TL" "TL") (list "_TC" "TC") (list "_TR" "TR")
                     (list "_ML" "ML") (list "_MC" "MC") (list "_MR" "MR")
                     (list "_BL" "BL") (list "_BC" "BC") (list "_BR" "BR"))
    (setq rowidx (1+ rowidx))
    (setq pt (list jc:anchor-x y 0.0))
    (setq ename (jc:mk-simple (car kw3) pt jc:text-h 0.0 jc:str-tt))
    (jc:mark pt) (jc:label rowidx y (cadr kw3))
    (jc:anchor-row f rowidx (cadr kw3) (car pt) (cadr pt) nil nil y)
    (jc:dump-row fd rowidx (cadr kw3) jc:str-tt ename "arial_tt" nil)
    (setq y (- y jc:row-h)))

  ;; ---- 16~18: SHX(ビッグフォント)。Left/Center/TR の代表3種 ----
  (setvar "TEXTSTYLE" "txt_ext2")
  (foreach kw4 (list (list nil "Left") (list "_C" "Center") (list "_TR" "TR"))
    (setq rowidx (1+ rowidx))
    (setq pt (list jc:anchor-x y 0.0))
    (setq ename (jc:mk-simple (car kw4) pt jc:text-h 0.0 jc:str-shx))
    (jc:mark pt) (jc:label rowidx y (strcat "SHX-" (cadr kw4)))
    (jc:anchor-row f rowidx (strcat "SHX-" (cadr kw4)) (car pt) (cadr pt) nil nil y)
    (jc:dump-row fd rowidx (strcat "SHX-" (cadr kw4)) jc:str-shx ename "txt_ext2" T)
    (setq y (- y jc:row-h)))

  ;; ---- 19: 日本語 TrueType（Center） ----
  ;; アンカーX は本体列と別の 160 に置く（直前行 SHX-TR のインクはアンカーより下へ
  ;; 10.4mm 伸び、本行のインクは上へ 8.4mm 伸びるため、同一X列では計測帯が重なる。
  ;; X をずらし、比較側も行ごとのX範囲で切る）
  (setvar "TEXTSTYLE" "msgothic_tt")
  (setq rowidx (1+ rowidx))
  (setq pt (list 160.0 y 0.0))
  (setq ename (jc:mk-simple "_C" pt jc:text-h 0.0 jc:str-jp))
  (jc:mark pt) (jc:label rowidx y "JP-Center(TT)")
  (jc:anchor-row f rowidx "JP-Center(TT)" (car pt) (cadr pt) nil nil y)
  (jc:dump-row fd rowidx "JP-Center(TT)" jc:str-jp ename "msgothic_tt" nil)
  (setq y (- y jc:row-h))

  ;; ---- 20~21: 回転×Justify（右側の空き領域）。回転時も Position が実描画の
  ;;             ベースライン始点を指すか（回転中心=位置合わせ点との相互作用）を見る ----
  (setvar "TEXTSTYLE" "arial_tt")
  (setq rowidx (1+ rowidx))
  (setq pt (list jc:rot-x 200.0 0.0))
  (setq ename (jc:mk-simple "_C" pt jc:text-h 30.0 jc:str-tt))
  (jc:mark pt)
  (jc:anchor-row f rowidx "Center-rot30" (car pt) (cadr pt) nil nil 200.0)
  (jc:dump-row fd rowidx "Center-rot30" jc:str-tt ename "arial_tt" nil)

  (setq rowidx (1+ rowidx))
  (setq pt (list jc:rot-x 130.0 0.0))
  (setq ename (jc:mk-simple "_TR" pt jc:text-h 30.0 jc:str-tt))
  (jc:mark pt)
  (jc:anchor-row f rowidx "TR-rot30" (car pt) (cadr pt) nil nil 130.0)
  (jc:dump-row fd rowidx "TR-rot30" jc:str-tt ename "arial_tt" nil)

  ;; ---- 22: Align 斜め2点（回転・高さとも導出） ----
  (setq rowidx (1+ rowidx))
  (setq pt (list jc:rot-x 40.0 0.0) pt2 (list (+ jc:rot-x 80.0) 80.0 0.0))
  (setq ename (jc:mk-align pt pt2 jc:str-tt))
  (jc:mark pt) (jc:mark pt2)
  (jc:anchor-row f rowidx "Align-diag" (car pt) (cadr pt) (car pt2) (cadr pt2) 40.0)
  (jc:dump-row fd rowidx "Align-diag" jc:str-tt ename "arial_tt" nil)

  (close f)
  (close fd)
  (command "_.REGEN")
  (princ (strcat "\nBUILDJUSTIFYCHART: " (itoa rowidx) " 行を生成し "
                 tsvpath " と " dumppath " へ書き出した。"))
  (princ))

(princ "\nbuild_justify_chart.lsp をロードしました。(c:BUILDJUSTIFYCHART \"アンカーTSV\" \"entgetダンプTSV\") で実行します。")
(princ)
