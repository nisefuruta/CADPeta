;;; probe_cmd.lsp
;;; TEXT コマンドの Justify 指定時のプロンプト列を実機で確認するための使い捨てプローブ。
;;; CMDECHO=1 の stdout ログと、生成された TEXT の entget ダンプを突き合わせて、
;;; (command "_.text" ...) に積む引数の個数・順序を確定する。
;;; 使い方: (load "...probe_cmd.lsp") の後 (c:PROBECMD "ダンプ出力先.txt")
(defun c:PROBECMD (dumppath / f ss n i e)
  ;; 単点系（Center）: 点 → 高さ → 回転角 → 文字列 → 空行(終了)
  (command "_.TEXT" "_J" "_C" (list 100.0 100.0 0.0) 10.0 0.0 "CenterTest" "")
  ;; 2点系・高さ導出（Align）: 始点 → 終点 → 文字列 → 空行(終了)
  (command "_.TEXT" "_J" "_A" (list 50.0 50.0 0.0) (list 150.0 50.0 0.0) "AlignTest" "")
  ;; 2点系・高さ指定（Fit）: 始点 → 終点 → 高さ → 文字列 → 空行(終了)
  (command "_.TEXT" "_J" "_F" (list 50.0 80.0 0.0) (list 150.0 80.0 0.0) 10.0 "FitTest" "")
  ;; 既定（左揃え、Justify無指定）: 点 → 高さ → 回転角 → 文字列 → 空行(終了)
  (command "_.TEXT" (list 20.0 20.0 0.0) 10.0 0.0 "LeftTest" "")

  (setq f (open dumppath "w"))
  (setq ss (ssget "_X" (list (cons 0 "TEXT"))))
  (setq n (if ss (sslength ss) 0))
  (write-line (strcat "count=" (itoa n)) f)
  (setq i 0)
  (while (< i n)
    (setq e (entget (ssname ss i)))
    (write-line (vl-prin1-to-string e) f)
    (setq i (1+ i)))
  (close f)
  (princ (strcat "\nPROBECMD: " (itoa n) " 件を " dumppath " へダンプした。"))
  (princ))
(princ "\nprobe_cmd.lsp をロードしました。(c:PROBECMD \"ダンプ出力先.txt\") で実行します。")
(princ)
