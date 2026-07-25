;;; PETADATA gradient E2E fixture generator (ASCII only).
;;; Creates the nine official gradient hatch names without ACI 7, so true-color
;;; stops remain authoritative in AutoCAD console and interactive sessions.

(defun c:GRADIENTE2E (/ names i col row x0 y0 x1 y1 name data)
  (setq names '("LINEAR" "CYLINDER" "INVCYLINDER"
                "SPHERICAL" "INVSPHERICAL" "HEMISPHERICAL"
                "INVHEMISPHERICAL" "CURVED" "INVCURVED"))
  (setq i 0)
  (foreach name names
    (setq col (rem i 3) row (fix (/ i 3)))
    (setq x0 (+ 10.0 (* col 70.0)) y0 (+ 10.0 (* row 50.0)))
    (setq x1 (+ x0 55.0) y1 (+ y0 35.0))
    (setq data (list
      '(0 . "HATCH") '(100 . "AcDbEntity") '(8 . "0")
      '(100 . "AcDbHatch") '(10 0.0 0.0 0.0) '(210 0.0 0.0 1.0)
      '(2 . "SOLID") '(70 . 1) '(71 . 0) '(91 . 1)
      '(92 . 1) '(93 . 4)
      '(72 . 1) (list 10 x0 y0) (list 11 x1 y0)
      '(72 . 1) (list 10 x1 y0) (list 11 x1 y1)
      '(72 . 1) (list 10 x1 y1) (list 11 x0 y1)
      '(72 . 1) (list 10 x0 y1) (list 11 x0 y0)
      '(97 . 0) '(75 . 0) '(76 . 1) '(98 . 1) '(10 0.0 0.0 0.0)
      '(450 . 1) '(451 . 0) (cons 460 (if (= (rem i 2) 0) 0.0 0.7853981634))
      (cons 461 (if (= row 2) 1.0 0.0))
      '(452 . 0) '(462 . 1.0) '(453 . 2)
      '(463 . 0.0) '(421 . 16711680)
      '(463 . 1.0) '(421 . 255) (cons 470 name)))
    (if (not (entmake data)) (princ (strcat "\nFAIL: " name)))
    (setq i (1+ i)))
  (command "_.ZOOM" "_E")
  (princ "\nGRADIENTE2E: 9 hatches created")
  (princ))
