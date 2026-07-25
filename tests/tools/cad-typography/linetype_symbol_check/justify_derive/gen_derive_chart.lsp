;; 端部整列アルゴリズムの実測導出用チャートを生成する。
;; 単純な整数mmパターン（JP15: dash10-gap5, JP24: dash12-gap5-dash6-gap1）を単一の
;; LINE・CIRCLE・ARC・LWPOLYLINE へ適用し、様々な長さ・円周・セグメント構成を並べる。
;; 座標はブロックごとに離して配置し、抽出後の行識別を Y/X 座標だけで一意にできるようにする。
(defun c:GENDERIVE ( / lens y i len holo hy c i2 diam cy ay)
  (command "-linetype" "L" "JP15" "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltjust\\test_justify.lin" "")
  (command "-linetype" "L" "JP24" "C:\\Users\\wk\\AppData\\Local\\Temp\\cadpeta_p3b_ltjust\\test_justify.lin" "")
  (setvar "LTSCALE" 1.0)
  (setvar "CELTSCALE" 1.0)
  (setvar "PLINEGEN" 0)

  ;; ---- ブロックA: 直線・JP15・導出用（Y=0..280、20刻み） ----
  (command "-linetype" "S" "JP15" "")
  (setq lens (list 3 7 10 13 15 20 22.5 30 37 45 50 60 70 100 142))
  (setq y 0)
  (foreach len lens
    (command "_LINE" (list 0 y) (list len y) "")
    (setq y (+ y 20))
  )

  ;; ---- ブロックB: 直線・JP15・ホールドアウト（Y=300..420、20刻み） ----
  (setq holo (list 5 18 26 33 48 65 118))
  (setq y 300)
  (foreach len holo
    (command "_LINE" (list 0 y) (list len y) "")
    (setq y (+ y 20))
  )

  ;; ---- ブロックC: 直線・JP24（非対称パターン、P=24。Y=440..520） ----
  (command "-linetype" "S" "JP24" "")
  (setq y 440)
  (foreach len (list 24 36 50 80 100)
    (command "_LINE" (list 0 y) (list len y) "")
    (setq y (+ y 20))
  )

  ;; ---- ブロックD: 円・JP15・導出用（目標円周C=40,45,50,60,70,100,110。X=700、Y=0..360、60刻み） ----
  (command "-linetype" "S" "JP15" "")
  (setq y 0)
  (foreach c (list 40 45 50 60 70 100 110)
    (setq diam (/ c pi))
    (command "_CIRCLE" (list 700 y) "D" diam)
    (setq y (+ y 60))
  )

  ;; ---- ブロックE: 円・JP15・ホールドアウト（目標C=55,85,130。X=700、Y=440..560、60刻み） ----
  (setq y 440)
  (foreach c (list 55 85 130)
    (setq diam (/ c pi))
    (command "_CIRCLE" (list 700 y) "D" diam)
    (setq y (+ y 60))
  )

  ;; ---- ブロックF: 円弧・JP15（半径30、90度=π/2の開曲線。中心(900,700)、始角0→終角90） ----
  (command "_ARC" "C" (list 900 700) (list 930 700) "A" 90)

  ;; ---- ブロックG: LWPOLYLINE・JP15・Plinegen=0（既定）。2頂点=2セグメント（水平20＋垂直下37） ----
  (setvar "PLINEGEN" 0)
  (command "_PLINE" (list 1000 0) (list 1020 0) (list 1020 -37) "")

  ;; ---- ブロックH: LWPOLYLINE・JP15・Plinegen=1（同一形状、Y+100オフセット） ----
  (setvar "PLINEGEN" 1)
  (command "_PLINE" (list 1000 100) (list 1020 100) (list 1020 63) "")
  (setvar "PLINEGEN" 0)

  (command "-linetype" "S" "BYLAYER" "")
  (princ)
)
(c:GENDERIVE)
