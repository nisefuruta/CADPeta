;;; ============================================================
;;; gen_testchart.lsp
;;; CADPeta Phase 0 検証用テストチャート図面の生成スクリプト
;;;
;;; 設計書§3.2「エンティティからIRへの対応」の各行を1行とし、
;;; 横軸に属性バリエーション（ACI / TrueColor / ByLayer / 透過 /
;;; 線種 / 線幅）を展開したグリッドを描く。
;;;
;;; 使い方:
;;;   1. mm系テンプレート（acadiso.dwt 等）で新規図面を開く
;;;   2. APPLOAD で本ファイルをロードし、コマンド TESTCHART を実行
;;;   3. 生成された図面をベクタPDFへプロットする（README.md 参照）
;;; ============================================================

(vl-load-com)

;; ---- レイアウト定数（mm） ----------------------------------
(setq tc:frame-w 400.0          ; 外枠の幅（実寸検証の基準を兼ねる）
      tc:frame-h 280.0          ; 外枠の高さ
      tc:col0-x  48.0           ; 1列目の左端X
      tc:col-w   58.0           ; 列ピッチ
      tc:row0-y  258.0          ; グリッド上端Y
      tc:row-h   12.0           ; 行ピッチ
      tc:hatch-scale-ansi 1.0   ; ANSI31 の尺度
      tc:hatch-scale-grv  0.2)  ; GRAVEL の尺度（粗密はここで調整）
;; 注: 複雑ハッチは当初 AR-CONC としたが、AR-* 系は実寸(インチ)基準の
;; パターンのため 52×9mm セルでは密度上限(HPMAXLINES)超過でソリッド化
;; するか、模様がセルより大きくなるかの二択となり成立しない。
;; 不規則な多数パスという検証意図が同じ GRAVEL に置き換えた。

;; ---- 汎用ヘルパ --------------------------------------------
(defun tc:pt (x y) (list x y 0.0))

(defun tc:+ (p dx dy)
  (list (+ (car p) dx) (+ (cadr p) dy) 0.0))

;; 実行中コマンドの残プロンプトをEnterで畳む（版差異の吸収）
;; CMDACTIVE はビット4がスクリプト実行中を示し、accoreconsole 等の
;; スクリプト経由では常に立ったままになる。通常(1)・透過(2)コマンドの
;; ビットだけを判定しないと無限ループする。上限回数は保険
(defun tc:flush ( / n)
  (setq n 0)
  (while (and (> (logand (getvar "CMDACTIVE") 3) 0) (< n 25))
    (command "")
    (setq n (1+ n))))

;; 例外を握りつぶしてsetvarする（版によって存在しないシステム変数用）
(defun tc:setvar-safe (name val)
  (vl-catch-all-apply 'setvar (list name val)))

;; ---- テーブル定義（レイヤ・文字スタイル・線種・ブロック） --
(defun tc:mklayer (name color)
  (if (not (tblsearch "LAYER" name))
    (entmake (list '(0 . "LAYER") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbLayerTableRecord")
                   (cons 2 name) (cons 62 color) '(70 . 0)
                   '(6 . "Continuous")))))

(defun tc:mkstyle (name font big width oblique)
  (if (not (tblsearch "STYLE" name))
    (entmake (list '(0 . "STYLE") '(100 . "AcDbSymbolTableRecord")
                   '(100 . "AcDbTextStyleTableRecord")
                   (cons 2 name) (cons 3 font) (cons 4 (if big big ""))
                   '(70 . 0) '(40 . 0.0) (cons 41 width) (cons 50 oblique)
                   '(71 . 0) '(42 . 2.5)))))

(defun tc:load-dashed ( / lin)
  (if (not (tblsearch "LTYPE" "DASHED"))
    (progn
      (setq lin (cond ((findfile "acadiso.lin")) ((findfile "acad.lin"))))
      (if lin
        (progn (command "_.-LINETYPE" "_L" "DASHED" lin) (tc:flush))))))

(defun tc:defblocks ()
  ;; TC_BLK: ByBlock色の円と対角線 ＋ 固定ACI1の対角線
  ;; （固定色が挿入色に引きずられないことも同時に検証する）
  (if (not (tblsearch "BLOCK" "TC_BLK"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "TC_BLK") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "CIRCLE") (10 0.0 0.0 0.0) (40 . 3.0) (62 . 0)))
      (entmake '((0 . "LINE") (10 -4.0 -3.0 0.0) (11 4.0 3.0 0.0) (62 . 0)))
      (entmake '((0 . "LINE") (10 -4.0 3.0 0.0) (11 4.0 -3.0 0.0) (62 . 1)))
      (entmake '((0 . "ENDBLK")))))
  ;; TC_NEST: TC_BLK を尺度・回転付きで2つ含む入れ子ブロック
  ;; （変換行列の合成を検証する）
  (if (not (tblsearch "BLOCK" "TC_NEST"))
    (progn
      (entmake '((0 . "BLOCK") (2 . "TC_NEST") (70 . 0) (10 0.0 0.0 0.0)))
      (entmake '((0 . "INSERT") (2 . "TC_BLK") (10 -6.0 0.0 0.0)
                 (41 . 0.6) (42 . 0.6) (43 . 0.6) (50 . 0.5236)))
      (entmake '((0 . "INSERT") (2 . "TC_BLK") (10 6.0 0.0 0.0)
                 (41 . 0.8) (42 . 0.8) (43 . 0.8)))
      (entmake '((0 . "ENDBLK"))))))

;; ---- ラベル描画（CHART_LABEL レイヤ、日本語TrueType） ------
;; 注: entmake は 62/440 を省略すると現在の CECOLOR/CETRANSPARENCY を
;; 明示属性として焼き込む（2026-07-06 実測）。
;; ラベルが列4の赤・透過50%を引き継がないよう ByLayer・不透明を明示する。
;; 既存コーパス（TESTCHART2-off/on.pdf）はこの修正前の生成物であり、
;; 一部の「-」が赤・透過50%を持つ（詳細は README.md）
(defun tc:text (p h str)
  (entmake (list '(0 . "TEXT") (cons 10 p) (cons 40 h) (cons 1 str)
                 '(7 . "CHART_TT_JP") '(8 . "CHART_LABEL")
                 '(62 . 256) '(440 . 33554687))))

;; ---- 列ごとの属性状態 --------------------------------------
;; 列1: ACI 1（赤）        列2: TrueColor 30,144,255
;; 列3: ByLayer（緑レイヤ） 列4: 透過50%（色はACI 1）
;; 列5: 線種 DASHED        列6: 線幅 0.53mm
(defun tc:colstate (col)
  (setvar "CLAYER" "CHART_OBJ")
  (setvar "CECOLOR" "BYLAYER")
  (setvar "CELTYPE" "Continuous")
  (setvar "CELWEIGHT" -1)
  (tc:setvar-safe "CETRANSPARENCY" 0)
  (cond
    ((= col 1) (setvar "CECOLOR" "1"))
    ((= col 2) (setvar "CECOLOR" "RGB:30,144,255"))
    ((= col 3) (setvar "CLAYER" "CHART_BYLAYER"))
    ((= col 4) (setvar "CECOLOR" "1") (tc:setvar-safe "CETRANSPARENCY" 50))
    ((= col 5) (setvar "CECOLOR" "1")
               (if (tblsearch "LTYPE" "DASHED") (setvar "CELTYPE" "DASHED")))
    ((= col 6) (setvar "CECOLOR" "1") (setvar "CELWEIGHT" 53))))

;; ---- 行ごとの描画関数（p = セル内容域の左下、幅52×高9mm） --
(defun tc:r-line (p)
  (command "_.LINE" (tc:+ p 0 0.5) (tc:+ p 52 8.5) ""))

(defun tc:r-pline (p)
  ;; 直線＋円弧セグメント＋全体幅0.6のポリライン
  (command "_.PLINE" (tc:+ p 0 2) "_W" 0.6 0.6 (tc:+ p 15 7)
           "_A" (tc:+ p 30 2) "_L" (tc:+ p 52 7) "")
  (tc:flush))

(defun tc:r-circle (p)
  (command "_.CIRCLE" (tc:+ p 26 4.5) 4.2))

(defun tc:r-arc (p)
  (command "_.ARC" (tc:+ p 2 1) (tc:+ p 26 8) (tc:+ p 50 1)))

(defun tc:r-ellipse (p)
  (command "_.ELLIPSE" "_C" (tc:+ p 26 4.5) (tc:+ p 50 4.5) 4.0)
  (tc:flush))

(defun tc:r-earc (p)
  (command "_.ELLIPSE" "_A" (tc:+ p 4 2) (tc:+ p 48 2) 6.0 0 180)
  (tc:flush))

(defun tc:r-spline (p)
  (command "_.SPLINE" (tc:+ p 0 2) (tc:+ p 13 8) (tc:+ p 26 1)
           (tc:+ p 39 8) (tc:+ p 52 2) "")
  (tc:flush))

(defun tc:textcell (style str p)
  (command "_.-TEXT" "_S" style (tc:+ p 1 2) 3.5 0 str)
  (tc:flush))

(defun tc:r-text-tt   (p) (tc:textcell "CHART_TT"     "Abc 123"      p))
(defun tc:r-text-ttjp (p) (tc:textcell "CHART_TT_JP"  "あ亜Ａ１ 施工図" p))
(defun tc:r-text-ttw  (p) (tc:textcell "CHART_TT_W"   "Abc 123"      p))
(defun tc:r-text-shx  (p) (tc:textcell "CHART_SHX"    "Abc 123"      p))
(defun tc:r-text-big  (p) (tc:textcell "CHART_SHX_JP" "図面 ABC"     p))

(defun tc:hatchcell (pat scale ang p / e)
  (command "_.PLINE" (tc:+ p 1 0.5) (tc:+ p 51 0.5)
           (tc:+ p 51 8.5) (tc:+ p 1 8.5) "_C")
  (setq e (entlast))
  (if (= pat "SOLID")
    (command "_.-HATCH" "_P" "SOLID" "_S" e "" "")
    (command "_.-HATCH" "_P" pat scale ang "_S" e "" ""))
  (tc:flush))

(defun tc:r-hatch-solid (p) (tc:hatchcell "SOLID" nil nil p))
(defun tc:r-hatch-ansi  (p) (tc:hatchcell "ANSI31" tc:hatch-scale-ansi 0 p))
(defun tc:r-hatch-grv   (p) (tc:hatchcell "GRAVEL" tc:hatch-scale-grv 0 p))

(defun tc:r-wipeout (p)
  ;; 下地: 横線3本と円
  (command "_.LINE" (tc:+ p 0 2) (tc:+ p 52 2) "")
  (command "_.LINE" (tc:+ p 0 4.5) (tc:+ p 52 4.5) "")
  (command "_.LINE" (tc:+ p 0 7) (tc:+ p 52 7) "")
  (command "_.CIRCLE" (tc:+ p 14 4.5) 4.0)
  ;; ワイプアウトで中央〜右を隠す
  (command "_.WIPEOUT" (tc:+ p 20 0.5) (tc:+ p 44 0.5)
           (tc:+ p 44 8.5) (tc:+ p 20 8.5) "")
  (tc:flush)
  ;; マスクより後の描画順で載る線（マスクの上に見えるはず）
  (command "_.LINE" (tc:+ p 18 1) (tc:+ p 46 8) ""))

(defun tc:r-insert (p)
  ;; 回転30°・尺度1.2（変換行列の検証を兼ねる）
  (command "_.-INSERT" "TC_BLK" (tc:+ p 26 4.5) 1.2 "" 30)
  (tc:flush))

(defun tc:r-nest (p)
  (command "_.-INSERT" "TC_NEST" (tc:+ p 26 4.5) 1 "" 345)
  (tc:flush))

(defun tc:r-dim (p)
  (command "_.DIMLINEAR" (tc:+ p 6 1) (tc:+ p 46 1) (tc:+ p 26 7))
  (tc:flush))

;; ---- 枠・見出し・目盛 --------------------------------------
(defun tc:frame ()
  (setvar "CLAYER" "CHART_FRAME")
  (setvar "CECOLOR" "BYLAYER")
  (command "_.PLINE" (tc:pt 0 0) (tc:pt tc:frame-w 0)
           (tc:pt tc:frame-w tc:frame-h) (tc:pt 0 tc:frame-h) "_C")
  (tc:text (tc:pt 5 270) 4.0 "CADPeta Phase 0 テストチャート（外枠 400×280mm）")
  ;; 100mm水平目盛（実寸検証用）
  (command "_.LINE" (tc:pt 5 20) (tc:pt 105 20) "")
  (foreach i '(0 1 2 3 4 5 6 7 8 9 10)
    (command "_.LINE" (tc:pt (+ 5 (* i 10.0)) 20)
             (tc:pt (+ 5 (* i 10.0)) (if (= 0 (rem i 5)) 24 22)) ""))
  (tc:text (tc:pt 4 15) 2.5 "0")
  (tc:text (tc:pt 52 15) 2.5 "50")
  (tc:text (tc:pt 100 15) 2.5 "100mm")
  (tc:text (tc:pt 120 19) 2.5 "目盛100mm・外枠400×280mm を実寸再現（FR-6）の基準とする"))

(defun tc:headers ( / c names)
  (setq names '("1: ACI 1 (赤)" "2: TrueColor 30,144,255" "3: ByLayer (緑レイヤ)"
                "4: 透過 50%" "5: 線種 DASHED" "6: 線幅 0.53mm")
        c 0)
  (foreach n names
    (tc:text (tc:pt (+ tc:col0-x (* c tc:col-w) 1) 260.5) 2.2 n)
    (setq c (1+ c))))

;; ---- メイン ------------------------------------------------
(defun c:TESTCHART ( / old-cmdecho old-clayer old-cecolor old-celtype
                       old-celweight old-osmode old-textstyle old-pellipse
                       *error* rows r c p row)
  (defun *error* (msg)
    (tc:flush)
    (setvar "CMDECHO" old-cmdecho) (setvar "OSMODE" old-osmode)
    (setvar "CLAYER" old-clayer) (setvar "CECOLOR" old-cecolor)
    (setvar "CELTYPE" old-celtype) (setvar "CELWEIGHT" old-celweight)
    (setvar "TEXTSTYLE" old-textstyle) (setvar "PELLIPSE" old-pellipse)
    (if (and msg (/= msg "Function cancelled"))
      (princ (strcat "\nTESTCHART エラー: " msg)))
    (princ))

  (setq old-cmdecho (getvar "CMDECHO") old-clayer (getvar "CLAYER")
        old-cecolor (getvar "CECOLOR") old-celtype (getvar "CELTYPE")
        old-celweight (getvar "CELWEIGHT") old-osmode (getvar "OSMODE")
        old-textstyle (getvar "TEXTSTYLE") old-pellipse (getvar "PELLIPSE"))
  (setvar "CMDECHO" 0)
  (setvar "OSMODE" 0)           ; オブジェクトスナップの介入を防ぐ
  (setvar "PELLIPSE" 0)         ; 真の楕円を生成する
  (tc:setvar-safe "TRANSPARENCYDISPLAY" 1)
  (tc:setvar-safe "WIPEOUTFRAME" 2)  ; 枠は表示するがプロットしない
  (tc:setvar-safe "LWDISPLAY" 1)

  (command "_.UNDO" "_BE")

  ;; テーブル準備
  (tc:mklayer "CHART_FRAME" 7)
  (tc:mklayer "CHART_LABEL" 8)
  (tc:mklayer "CHART_OBJ" 7)
  (tc:mklayer "CHART_BYLAYER" 3)
  (tc:mkstyle "CHART_TT" "arial.ttf" nil 1.0 0.0)
  (tc:mkstyle "CHART_TT_JP" "msgothic.ttc" nil 1.0 0.0)
  ;; 幅係数≠1.0/傾斜付きTrueTypeはPDFプロットでジオメトリ化される
  ;; （Autodesk公式）。その実測用の変形スタイル
  (tc:mkstyle "CHART_TT_W" "arial.ttf" nil 0.8 15.0)
  (tc:mkstyle "CHART_SHX" "romans.shx" nil 1.0 0.0)
  (tc:mkstyle "CHART_SHX_JP" "romans.shx" "extfont.shx" 1.0 0.0)
  (tc:load-dashed)
  (tc:defblocks)

  ;; 行定義: (ラベル 描画関数 適用列)
  ;; 適用外の組み合わせ（文字×線種等）は "-" 表示で明示する
  (setq rows (list
    (list "線分 LINE"                 'tc:r-line        '(1 2 3 4 5 6))
    (list "ポリライン(円弧・幅0.6)"   'tc:r-pline       '(1 2 3 4 5 6))
    (list "円 CIRCLE"                 'tc:r-circle      '(1 2 3 4 5 6))
    (list "円弧 ARC"                  'tc:r-arc         '(1 2 3 4 5 6))
    (list "楕円 ELLIPSE"              'tc:r-ellipse     '(1 2 3 4 5 6))
    (list "楕円弧 ELLIPSE-A"          'tc:r-earc        '(1 2 3 4 5 6))
    (list "スプライン SPLINE"         'tc:r-spline      '(1 2 3 4 5 6))
    (list "文字 TrueType (Arial)"     'tc:r-text-tt     '(1 2 3 4))
    (list "文字 TrueType 日本語"      'tc:r-text-ttjp   '(1 2 3 4))
    (list "文字 TT 変形(幅0.8/斜15)"  'tc:r-text-ttw    '(1 2 3 4))
    (list "文字 SHX (romans)"         'tc:r-text-shx    '(1 2 3 4))
    (list "文字 SHX+BigFont"          'tc:r-text-big    '(1 2 3 4))
    (list "ハッチ SOLID"              'tc:r-hatch-solid '(1 2 3 4))
    (list "ハッチ ANSI31"             'tc:r-hatch-ansi  '(1 2 3 4))
    (list "ハッチ GRAVEL"             'tc:r-hatch-grv   '(1 2 3 4))
    (list "ワイプアウト"              'tc:r-wipeout     '(1))
    (list "ブロック参照 (ByBlock)"    'tc:r-insert      '(1 2 3))
    (list "入れ子ブロック"            'tc:r-nest        '(1 2 3))
    (list "寸法 DIMLINEAR"            'tc:r-dim         '(1 3))))

  ;; 枠と見出し
  (tc:frame)
  (tc:headers)

  ;; グリッド本体
  (setq r 0)
  (foreach row rows
    (setq r (1+ r))
    (tc:text (tc:pt 4 (+ (- tc:row0-y (* r tc:row-h)) 3)) 2.2 (car row))
    (setq c 0)
    (repeat 6
      (setq c (1+ c)
            p (tc:pt (+ tc:col0-x (* (1- c) tc:col-w))
                     (- tc:row0-y (* r tc:row-h))))
      (if (member c (caddr row))
        (progn (tc:colstate c) (apply (cadr row) (list p)))
        (tc:text (tc:+ p 24 3) 2.5 "-"))))

  (command "_.UNDO" "_E")
  (command "_.ZOOM" "_E")

  ;; 後始末
  (setvar "CMDECHO" old-cmdecho) (setvar "OSMODE" old-osmode)
  (setvar "CLAYER" old-clayer) (setvar "CECOLOR" old-cecolor)
  (setvar "CELTYPE" old-celtype) (setvar "CELWEIGHT" old-celweight)
  (setvar "TEXTSTYLE" old-textstyle) (setvar "PELLIPSE" old-pellipse)
  (princ "\nテストチャートを生成しました。README.md の手順でPDFへプロットしてください。")
  (princ))

(princ "\ngen_testchart.lsp をロードしました。TESTCHART で実行します。")
(princ)
