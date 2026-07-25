;;; gh_charts.lsp -- gradient hatch ground-truth chart generator (ASCII only)
;;; Creates one gradient hatch (rect boundary 40,40 - 380,260 mm) via entmake
;;; and appends an entget dump to <gh:dir>/charts.log as machine evidence.
;;; gh:dir must be set (setq gh:dir "...") by the calling .scr before (load).
;;; Colors are 421 true-color integers (0xRRGGBB). Boundary is edge-form
;;; (92=1, 93=4, 72=1 per edge) which entmake accepts on AutoCAD 2026 console
;;; (verified 2026-07-13).

(defun gh:w (s / f)
  (if (not gh:log) (setq gh:log (strcat gh:dir "/charts.log")))
  (setq f (open gh:log "a"))
  (if f (progn (write-line s f) (close f))))

(defun gh:dump1 (ed c / v)
  (setq v (cdr (assoc c ed)))
  (cond ((= (type v) 'STR) v)
        ((= (type v) 'INT) (itoa v))
        ((= (type v) 'REAL) (rtos v 2 6))
        (t "(none)")))

;;; aci1/aci2: ACI code for each stop, or -1 to omit the 63 group.
;;; NOTE (measured 2026-07-13): with 63=7 on both stops the plot renders a
;;; uniform color regardless of GFNAME/421 -- the ACI wins over true color.
(defun gh:prep (gname ang shift rgb1 rgb2 aci1 aci2 / ss ok ed x0 y0 x1 y1 lst)
  (setq x0 40.0 y0 40.0 x1 380.0 y1 260.0)
  (setq ss (ssget "_X"))
  (if ss (command "_.ERASE" ss ""))
  (setq lst (list
    '(0 . "HATCH") '(100 . "AcDbEntity") '(8 . "0")
    '(100 . "AcDbHatch")
    '(10 0.0 0.0 0.0) '(210 0.0 0.0 1.0)
    '(2 . "SOLID") '(70 . 1) '(71 . 0)
    '(91 . 1)
    '(92 . 1) '(93 . 4)
    '(72 . 1) (list 10 x0 y0) (list 11 x1 y0)
    '(72 . 1) (list 10 x1 y0) (list 11 x1 y1)
    '(72 . 1) (list 10 x1 y1) (list 11 x0 y1)
    '(72 . 1) (list 10 x0 y1) (list 11 x0 y0)
    '(97 . 0)
    '(75 . 0) '(76 . 1)
    '(98 . 1) '(10 0.0 0.0 0.0)
    '(450 . 1) '(451 . 0)
    (cons 460 ang) (cons 461 shift)
    '(452 . 0) '(462 . 1.0) '(453 . 2)
    '(463 . 0.0)))
  (if (>= aci1 0) (setq lst (append lst (list (cons 63 aci1)))))
  (setq lst (append lst (list (cons 421 rgb1) '(463 . 1.0))))
  (if (>= aci2 0) (setq lst (append lst (list (cons 63 aci2)))))
  (setq lst (append lst (list (cons 421 rgb2) (cons 470 gname))))
  (setq ok (entmake lst))
  (gh:w (strcat gname " ang=" (rtos ang 2 6) " shift=" (rtos shift 2 3)
                " rgb=" (itoa rgb1) "/" (itoa rgb2)
                " entmake=" (if ok "OK" "FAIL")))
  (if ok
    (progn
      (setq ed (entget (entlast)))
      (gh:w (strcat "  450=" (gh:dump1 ed 450) " 452=" (gh:dump1 ed 452)
                    " 453=" (gh:dump1 ed 453) " 460=" (gh:dump1 ed 460)
                    " 461=" (gh:dump1 ed 461) " 470=" (gh:dump1 ed 470)))))
  (princ))
