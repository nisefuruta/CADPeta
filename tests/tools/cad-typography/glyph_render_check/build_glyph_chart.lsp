;;; ============================================================
;;; build_glyph_chart.lsp
;;; SHX 解釈器の実機突き合わせ用チャート図面を作る。
;;; 文字スタイル4本（romans / txt_ext2 / txt_ext / txt_big）で
;;; 既知位置・既知高さの DBText を並べ、見当枠（LWPOLYLINE）を描く。
;;; 全エンティティの実配置を TSV へ書き出し、プローブ側（C#）の入力にする。
;;;
;;; 使い方: (load "...build_glyph_chart.lsp") の後 (BUILDGLYPHCHART "出力TSVパス")
;;; ============================================================

;; ---- 見当枠（左下・右上、mm。プローブ側と同一座標を共有する定数） ----
(setq gc:frame-x0 10.0 gc:frame-y0 10.0
      gc:frame-x1 410.0 gc:frame-y1 287.0)

;; ---- 行レイアウト定数 ----
(setq gc:left-x 20.0        ; 各行の左端X
      gc:row0-y 270.0       ; 1行目のY
      gc:row-h  25.0        ; 行ピッチ
      gc:text-h 10.0)       ; 文字高さ（キャップハイト、mm）

;; ---- フォントファイルの絶対パス ----
(setq gc:fonts-dir "C:/Program Files/Autodesk/AutoCAD 2026/Fonts/")

;; スタイル定義: (スタイル名 メインSHX ビッグフォントSHXまたはnil)
(setq gc:styles (list
  (list "romans"   (strcat gc:fonts-dir "romans.shx")  nil)
  (list "txt_ext2" (strcat gc:fonts-dir "txt.shx")      (strcat gc:fonts-dir "extfont2.shx"))
  (list "txt_ext"  (strcat gc:fonts-dir "txt.shx")      (strcat gc:fonts-dir "extfont.shx"))
  (list "txt_big"  (strcat gc:fonts-dir "txt.shx")      (strcat gc:fonts-dir "bigfont.shx"))))

;; 行定義: (スタイル名 文字列)。上から順に gc:row0-y から gc:row-h 刻みで下る
(setq gc:rows (list
  (list "romans"   "ABCDEFGHIJKLMNOPQRSTUVWXYZ")
  (list "romans"   "abcdefghijklmnopqrstuvwxyz")
  (list "romans"   "0123456789 SCO38&()@.,;:%")
  (list "txt_ext2" "亜仝機械検証図面文字寸法")
  (list "txt_ext2" "あいうえおアイウエオ")
  (list "txt_ext2" "ｱｲｳｴｵﾒｰﾄﾙ 半角ABC123")
  (list "txt_ext"  "亜仝機械検証図面文字")
  (list "txt_big"  "亜仝機械検証図面文字")))

(defun gc:style-lookup (name / e)
  (setq e (assoc name gc:styles))
  (if e (cdr e) (list nil nil)))

(defun gc:mkstyle (name font big)
  (if (not (tblsearch "STYLE" name))
    (entmake (list '(0 . "STYLE") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbTextStyleTableRecord")
                   (cons 2 name) (cons 3 font) (cons 4 (if big big ""))
                   '(70 . 0) '(40 . 0.0) '(41 . 1.0) '(50 . 0.0)
                   '(71 . 0) '(42 . 2.5)))))

(defun gc:mktext (style x y h str)
  (entmake (list '(0 . "TEXT") (list 10 x y 0.0) (cons 40 h) (cons 1 str)
                 '(50 . 0.0) '(41 . 1.0) (cons 7 style) '(8 . "0") '(62 . 7))))

(defun gc:mkframe ()
  (entmake (list '(0 . "LWPOLYLINE") '(100 . "AcDbEntity") '(100 . "AcDbPolyline")
                 (cons 90 4) (cons 70 1) '(62 . 7)
                 (list 10 gc:frame-x0 gc:frame-y0)
                 (list 10 gc:frame-x1 gc:frame-y0)
                 (list 10 gc:frame-x1 gc:frame-y1)
                 (list 10 gc:frame-x0 gc:frame-y1))))

(defun c:BUILDGLYPHCHART (tsvpath / f fontinfo shx big r style txt y)
  (foreach s gc:styles
    (gc:mkstyle (car s) (cadr s) (caddr s)))
  (gc:mkframe)

  (setq f (open tsvpath "w"))
  (write-line (strcat "#frame\t" (rtos gc:frame-x0 2 4) "\t" (rtos gc:frame-y0 2 4)
                       "\t" (rtos gc:frame-x1 2 4) "\t" (rtos gc:frame-y1 2 4)) f)

  (setq y gc:row0-y)
  (foreach r gc:rows
    (setq style (car r) txt (cadr r))
    (setq fontinfo (gc:style-lookup style))
    (setq shx (car fontinfo) big (cadr fontinfo))
    (gc:mktext style gc:left-x y gc:text-h txt)
    (write-line (strcat style "\t" shx "\t" (if big big "") "\t"
                         (rtos gc:left-x 2 6) "\t" (rtos y 2 6) "\t"
                         (rtos gc:text-h 2 6) "\t0\t1\t" txt) f)
    (setq y (- y gc:row-h)))

  (close f)
  (command "_.ZOOM" "_E")
  (princ (strcat "\nBUILDGLYPHCHART: " (itoa (length gc:rows)) " 行を生成し " tsvpath " へ書き出した。"))
  (princ))

(princ "\nbuild_glyph_chart.lsp をロードしました。BUILDGLYPHCHART \"TSVパス\" で実行します。")
(princ)
