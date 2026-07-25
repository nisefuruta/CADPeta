// 選択絞り込みの確認プロンプト（部分出力）。4製品の EditorPort が同文を複製していた
// のをここへ一本化する。IEditorPort.PromptString だけで構成し、CAD 型に依存しない。
namespace CadPeta.Plugin.Shared;

public static class SelectionConfirmPrompts
{
    /// <summary>非対応対象を除外した部分出力の確認。既定は No。</summary>
    public static bool ConfirmPartialSelection(IEditorPort editor, UnsupportedSelectionSummary summary) =>
        IsYes(editor.PromptString(Msg.Format("confirm.partial", summary.Unsupported.Count)));

    // 判定は言語に依らず Y / Yes だけを受ける（スクリプト・自動化が言語で割れないための固定）。
    static bool IsYes(string? value) => string.Equals(value?.Trim(), "Y", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value?.Trim(), "Yes", StringComparison.OrdinalIgnoreCase);
}
