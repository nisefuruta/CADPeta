;;; gradient_transform_fixture.lsp (ASCII only)
;;; 構造SVG書き出しのグラデーションのブロック変換耐性＋代表色縮退フォールバックを実機検査するための
;;; フィクスチャ。LINEAR グラデーションハッチをブロック GRADBLK に入れ、回転45/180・鏡像・
;;; 非等方・二重ネストで INSERT し、最後に未知 GFNAME("BOGUS") のハッチをトップレベルへ置く。
;;; 期待: 有効ブロック 5 個 -> IrImage 5 枚、BOGUS 1 個 -> 代表色縮退(ソリッド path)。

(defun gh:hatch (x0 y0 x1 y1 gname)
  (list
    '(0 . "HATCH") '(100 . "AcDbEntity") '(8 . "0")
    '(100 . "AcDbHatch") '(10 0.0 0.0 0.0) '(210 0.0 0.0 1.0)
    '(2 . "SOLID") '(70 . 1) '(71 . 0) '(91 . 1)
    '(92 . 1) '(93 . 4)
    '(72 . 1) (list 10 x0 y0) (list 11 x1 y0)
    '(72 . 1) (list 10 x1 y0) (list 11 x1 y1)
    '(72 . 1) (list 10 x1 y1) (list 11 x0 y1)
    '(72 . 1) (list 10 x0 y1) (list 11 x0 y0)
    '(97 . 0) '(75 . 0) '(76 . 1) '(98 . 1) '(10 0.0 0.0 0.0)
    '(450 . 1) '(451 . 0) '(460 . 0.0) '(461 . 0.0)
    '(452 . 0) '(462 . 1.0) '(453 . 2)
    '(463 . 0.0) '(421 . 16711680)
    '(463 . 1.0) '(421 . 255) (cons 470 gname)))

(defun gh:ins (bname x y sx sy rot)
  (entmake (list '(0 . "INSERT") '(100 . "AcDbEntity") '(8 . "0")
                 '(100 . "AcDbBlockReference") (cons 2 bname)
                 (list 10 x y 0.0) (cons 41 sx) (cons 42 sy) (cons 43 1.0)
                 (cons 50 rot))))

(defun c:GRADXFORM (/ )
  ;; --- block GRADBLK: 1 LINEAR gradient hatch, local 0..50 x 0..30 ---
  (entmake '((0 . "BLOCK") (100 . "AcDbEntity") (8 . "0")
             (100 . "AcDbBlockBegin") (2 . "GRADBLK") (70 . 0) (10 0.0 0.0 0.0)))
  (entmake (gh:hatch 0.0 0.0 50.0 30.0 "LINEAR"))
  (entmake '((0 . "ENDBLK") (100 . "AcDbBlockEnd") (8 . "0")))
  ;; --- block GRADBLK2: nests GRADBLK (double nest when inserted) ---
  (entmake '((0 . "BLOCK") (100 . "AcDbEntity") (8 . "0")
             (100 . "AcDbBlockBegin") (2 . "GRADBLK2") (70 . 0) (10 0.0 0.0 0.0)))
  (entmake (list '(0 . "INSERT") '(100 . "AcDbEntity") '(8 . "0")
                 '(100 . "AcDbBlockReference") '(2 . "GRADBLK")
                 (list 10 0.0 0.0 0.0) (cons 41 1.0) (cons 42 1.0) (cons 43 1.0) (cons 50 0.0)))
  (entmake '((0 . "ENDBLK") (100 . "AcDbBlockEnd") (8 . "0")))
  ;; --- inserts: rot45, rot180+scale2, mirror(x=-1), non-uniform(2x1) ---
  (gh:ins "GRADBLK"   10.0  10.0  1.0  1.0 0.7853981634)
  (gh:ins "GRADBLK"  120.0  10.0  2.0  2.0 3.1415926536)
  (gh:ins "GRADBLK"  260.0  10.0 -1.0  1.0 0.0)
  (gh:ins "GRADBLK"   10.0 120.0  2.0  1.0 0.0)
  ;; --- double nested insert, rotated ---
  (gh:ins "GRADBLK2" 200.0 120.0  1.0  1.0 0.7853981634)
  ;; --- unknown GFNAME -> representative-color fallback (top level) ---
  (entmake (gh:hatch 300.0 200.0 380.0 260.0 "BOGUS"))
  (command "_.ZOOM" "_E")
  (princ "\nGRADXFORM: 5 valid gradient inserts + 1 BOGUS created")
  (princ))
