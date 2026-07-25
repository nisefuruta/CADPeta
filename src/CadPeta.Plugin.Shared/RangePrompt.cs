// 範囲指定プロンプト（4製品共通の単一実装）: PETA/PETADIAG の冒頭で範囲とオプションを対話で確定する。
// 範囲（Enter/全体(A)=全体・2点コーナー=窓）は plot 処理する範囲で、先選択・オブジェクト(O)の
// 有無に依らず必ず確定する。オブジェクト(O)は範囲の中で出力対象を選択オブジェクトへ絞る
// 継続オプション（余白(M)等と同種）で、先選択は絞り込みの初期値になる。
// 表示文言は言語カタログ（Msg）から引く。キーワードの判定はグローバル名（All 等）のままで、
// 言語で割れない。
using System.IO;
using System.Globalization;
using CadPeta.Ir;

namespace CadPeta.Plugin.Shared;

public static class RangePrompt
{
    /// <summary>
    /// 範囲とオプションを対話で確定する。null は利用者キャンセル（コマンド中止）。
    /// </summary>
    public static RangeChoice? TryGetChoice(IEditorPort ed, TextWriter log, TextWriter verbose)
    {
        var choice = new RangeChoice();
        // 先選択（コマンド起動時に選択済みのオブジェクト）は絞り込みの初期値。範囲は必ず聞く
        var implied = ed.GetPickfirstSelection();
        if (implied != null)
        {
            choice.SelectionHandle = implied;
            log.WriteLine(Msg.Text("range.pickfirst_filter"));
        }
        return TryGetRange(ed, log, verbose, choice) ? choice : null;
    }

    /// <summary>
    /// 範囲（全体・窓）とオプション（絞り込み・余白・レイヤ・文字輪郭ほか）を対話で確定する。
    /// false はキャンセル。
    /// </summary>
    static bool TryGetRange(IEditorPort ed, TextWriter log, TextWriter verbose, RangeChoice choice)
    {
        var keywords = new[]
        {
            new PromptKeyword(PluginDefaults.KeywordAll, "A", Msg.Text("keyword.all")),
            new PromptKeyword(PluginDefaults.KeywordObject, "O", Msg.Text("keyword.object")),
            new PromptKeyword(PluginDefaults.KeywordMargin, "M", Msg.Text("keyword.margin")),
            new PromptKeyword(PluginDefaults.KeywordLayer, "L", Msg.Text("keyword.layer")),
            new PromptKeyword(PluginDefaults.KeywordTextOutline, "T", Msg.Text("keyword.text_outline")),
            new PromptKeyword(PluginDefaults.KeywordFileOutput, "F", Msg.Text("keyword.file_output")),
            new PromptKeyword(PluginDefaults.KeywordEmfBackground, "B", Msg.Text("keyword.emf_background")),
        };
        // 提示するキーワード列と既定（全体）は keywords 配列だけを情報源にして組み立てる
        string firstMsg = Msg.Format("range.first_corner",
            string.Join("/", keywords.Select(k => k.Display)), keywords[0].Display);
        string secondMsg = Msg.Text("range.second_corner");

        while (true)
        {
            // 窓 pick はペーパー座標系で行う（ビューポート内 MSPACE ならポートがペーパー空間へ切替）。
            // オブジェクト選択（O）は利用者の元の空間で行うため、切替は PromptWindow の間だけに閉じる
            PromptResult<PlotWindow> r;
            var pickScope = ed.BeginWindowPickScope(verbose);
            try
            {
                r = ed.PromptWindow(firstMsg, keywords, secondMsg);
            }
            catch (Exception original)
            {
                try { pickScope.Dispose(); }
                catch (Exception restore)
                {
                    throw new AggregateException("窓pick処理と空間復元の両方に失敗した", original, restore);
                }
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(original).Throw();
                throw;
            }
            pickScope.Dispose();
            switch (r.Outcome)
            {
                case PromptOutcome.None:
                    return true; // Enter → 全体
                case PromptOutcome.Cancelled:
                    log.WriteLine(Msg.Text("range.cancelled"));
                    return false;
                case PromptOutcome.Keyword:
                    if (r.Keyword == PluginDefaults.KeywordAll)
                        return true;
                    if (r.Keyword == PluginDefaults.KeywordObject)
                    {
                        // 絞り込みの指定・やり直し。空選択は絞り込み解除（先選択も外す）。
                        // 範囲は引き続きこのプロンプトで確定する
                        var sel = ed.PromptSelection();
                        if (sel.Outcome == SelectionOutcome.Cancelled)
                        {
                            log.WriteLine(Msg.Text("range.selection_cancelled"));
                            return false;
                        }
                        choice.SelectionHandle =
                            sel.Outcome == SelectionOutcome.Selected ? sel.Handle : null;
                        ed.WriteLine(Msg.Text(choice.IsSelection
                            ? "range.filter_set" : "range.filter_cleared"));
                        continue;
                    }
                    if (r.Keyword == PluginDefaults.KeywordMargin)
                    {
                        double margin = choice.MarginMm;
                        if (!TryPromptMargin(ed, log, ref margin))
                            return false;
                        choice.MarginMm = margin;
                        continue;
                    }
                    if (r.Keyword == PluginDefaults.KeywordLayer)
                    {
                        choice.GroupByLayer = !choice.GroupByLayer;
                        ed.WriteLine(Msg.Text(choice.GroupByLayer ? "toggle.layer.on" : "toggle.layer.off"));
                        continue;
                    }
                    if (r.Keyword == PluginDefaults.KeywordTextOutline)
                    {
                        choice.TextOutline = !choice.TextOutline;
                        ed.WriteLine(Msg.Text(choice.TextOutline ? "toggle.outline.on" : "toggle.outline.off"));
                        continue;
                    }
                    if (r.Keyword == PluginDefaults.KeywordFileOutput)
                    {
                        choice.FileOutput = !choice.FileOutput;
                        ed.WriteLine(Msg.Text(choice.FileOutput
                            ? "toggle.file_output.on" : "toggle.file_output.off"));
                        continue;
                    }
                    if (r.Keyword == PluginDefaults.KeywordEmfBackground)
                    {
                        if (!TryPromptEmfBackground(ed, log, choice)) return false;
                        continue;
                    }
                    log.WriteLine(Msg.Format("range.unknown_keyword", r.Keyword!));
                    continue;
                case PromptOutcome.Value:
                default:
                    var win = r.Value;
                    if (win.Width <= PluginDefaults.MinWindowSizeDu
                        || win.Height <= PluginDefaults.MinWindowSizeDu)
                    {
                        // 同一点の二度 pick 等。プロット窓として成立しないため聞き直す
                        ed.WriteLine(Msg.Text("range.degenerate_window"));
                        continue;
                    }
                    choice.Window = win;
                    return true;
            }
        }
    }

    public static RangeChoice? TryGetDataChoice(IEditorPort ed, TextWriter log, TextWriter verbose)
    {
        object? handle = ed.GetPickfirstSelection();
        if (handle == null)
        {
            var selected = ed.PromptSelection();
            if (selected.Outcome != SelectionOutcome.Selected)
            {
                log.WriteLine(Msg.Text(selected.Outcome == SelectionOutcome.Cancelled
                    ? "range.data_selection_cancelled"
                    : "range.data_selection_empty"));
                return null;
            }
            handle = selected.Handle;
        }
        else log.WriteLine(Msg.Text("range.pickfirst_data"));

        var choice = new RangeChoice { SelectionHandle = handle, FileOutput = true };
        return TryPromptScale(ed, log, choice) ? choice : null;
    }

    // 構造SVG書き出し（PETADATA）の尺度プロンプトに出す項目。表示名は現在の言語カタログから
    // 引くため、配列はキャッシュせず呼び出しごとに組み立てる。
    static PromptKeyword[] ScaleKeywords() => new PromptKeyword[]
    {
        new(PluginDefaults.KeywordMargin, "M", Msg.Text("keyword.margin")),
        new(PluginDefaults.KeywordLayer, "L", Msg.Text("keyword.layer")),
        new(PluginDefaults.KeywordTextOutline, "T", Msg.Text("keyword.text_outline")),
        new(PluginDefaults.KeywordShxSubstitute, "S", Msg.Text("keyword.shx_substitute")),
        new(PluginDefaults.KeywordGradientDpi, "G", Msg.Text("keyword.gradient")),
    };

    static bool TryPromptScale(IEditorPort ed, TextWriter log, RangeChoice choice)
    {
        var keywords = ScaleKeywords();
        while (true)
        {
            var optionText = string.Join("/", keywords.Select(k => k.Display));
            var msg = Msg.Format("scale.prompt.svg", optionText, choice.ScaleDenominator);
            var result = ed.PromptDouble(msg, keywords, choice.ScaleDenominator,
                                         useDefault: false, allowNone: true, allowZero: false,
                                         allowNegative: false);

            if (result.Outcome == PromptOutcome.None)
                return true; // Enter → 既定のまま
            if (result.Outcome == PromptOutcome.Value)
            {
                choice.ScaleDenominator = result.Value;
                return true;
            }
            if (result.Outcome == PromptOutcome.Keyword)
            {
                if (!keywords.Any(k => k.Global == result.Keyword))
                {
                    log.WriteLine(Msg.Format("range.unknown_keyword", result.Keyword!));
                    continue;
                }
                if (result.Keyword == PluginDefaults.KeywordMargin)
                {
                    double margin = choice.MarginMm;
                    if (!TryPromptMargin(ed, log, ref margin))
                        return false;
                    choice.MarginMm = margin;
                    continue;
                }
                if (result.Keyword == PluginDefaults.KeywordLayer)
                {
                    choice.GroupByLayer = !choice.GroupByLayer;
                    ed.WriteLine(Msg.Text(choice.GroupByLayer ? "toggle.layer.on" : "toggle.layer.off"));
                    continue;
                }
                if (result.Keyword == PluginDefaults.KeywordShxSubstitute)
                {
                    choice.ShxSubstitute = !choice.ShxSubstitute;
                    ed.WriteLine(Msg.Text(choice.ShxSubstitute ? "toggle.shx.on" : "toggle.shx.off"));
                    continue;
                }
                if (result.Keyword == PluginDefaults.KeywordTextOutline)
                {
                    choice.TextOutline = !choice.TextOutline;
                    ed.WriteLine(Msg.Text(choice.TextOutline ? "toggle.outline.on" : "toggle.outline.off"));
                    continue;
                }
                if (result.Keyword == PluginDefaults.KeywordGradientDpi)
                {
                    var dpi = ed.PromptDouble(Msg.Text("scale.gradient_dpi_prompt"), Array.Empty<PromptKeyword>(),
                        choice.GradientDpi, useDefault: true, allowNone: true, allowZero: false, allowNegative: false);
                    if (dpi.Outcome == PromptOutcome.Cancelled) return false;
                    if (dpi.Outcome == PromptOutcome.Value) choice.GradientDpi = dpi.Value;
                    ed.WriteLine(Msg.Format("scale.gradient_dpi_state", choice.GradientDpi));
                    continue;
                }
                log.WriteLine(Msg.Format("range.unknown_keyword", result.Keyword!));
                continue;
            }
            log.WriteLine(Msg.Text("scale.cancelled"));
            return false;
        }
    }

    /// <summary>余白（mm）の入力。キャンセルは false（コマンド中止）。</summary>
    static bool TryPromptMargin(IEditorPort ed, TextWriter log, ref double marginMm)
    {
        var result = ed.PromptDouble(Msg.Text("scale.margin_prompt"), Array.Empty<PromptKeyword>(),
                                     marginMm, useDefault: true, allowNone: true, allowZero: true,
                                     allowNegative: false);
        if (result.Outcome == PromptOutcome.Value)
        {
            marginMm = result.Value;
            return true;
        }
        if (result.Outcome == PromptOutcome.None)
            return true; // Enter=既定値のまま
        log.WriteLine(Msg.Text("scale.margin_cancelled"));
        return false;
    }

    static bool TryPromptEmfBackground(IEditorPort ed, TextWriter log, RangeChoice choice)
    {
        // 現在値をそのまま既定表示にする。空入力（Enter）は現在値を維持する。
        string? input = ed.PromptString(Msg.Format("scale.emf_bg_prompt", choice.EmfBackground.ToHex()));
        if (input == null) { log.WriteLine(Msg.Text("scale.emf_bg_cancelled")); return false; }
        if (input.Length == 0) return true;
        // NumberStyles.HexNumber は前後の空白も許すため AllowHexSpecifier のみへ厳格化し、
        // 6桁ちょうどの16進以外（空白混じり等）を弾く。
        if (input.Length != 7 || input[0] != '#'
            || !int.TryParse(input.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int rgb))
        {
            ed.WriteLine(Msg.Text("scale.emf_bg_invalid"));
            return true;
        }
        choice.EmfBackground = new IrRgb(((rgb >> 16) & 255) / 255.0,
                                         ((rgb >> 8) & 255) / 255.0,
                                         (rgb & 255) / 255.0);
        ed.WriteLine(Msg.Format("scale.emf_bg_state", choice.EmfBackground.ToHex()));
        return true;
    }
}
