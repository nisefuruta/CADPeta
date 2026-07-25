namespace CadPeta.UseCases;

/// <summary>
/// PETA 本体（プラグイン）の末尾処理を一箇所へ集約する。取得系統・ホスト CAD に依らない
/// 「クリップボード登録失敗時の回復判断」「利用者への案内文」「結果サマリの出力」を
/// 4プラグイン共通で単一実装にし、文言や回復手順のドリフトを防ぐ。
/// </summary>
public static class PetaOutcome
{
    /// <summary>
    /// クリップボード登録失敗時に、必要なら一時 EMF を回復場所へ退避する。
    /// 返り値は「利用者が使える EMF の実パス」。ファイル出力ありのときは出力済みの
    /// EMF がそのまま使えるため退避せず null を返す（呼び出し元は元の emfPath を案内する）。
    /// ファイル出力オフのときは EMF を回復場所へ移し、移動できなければ一時フォルダごと
    /// 残す（<paramref name="scope"/>.PreserveOnDispose）ことで、どちらでも EMF を回収できる。
    /// </summary>
    public static string? RecoverIfNeeded(ArtifactScope scope, string emfPath,
                                          bool effectiveFileOutput, Action<string> warn)
    {
        if (effectiveFileOutput)
            return null; // 出力済み EMF がそのまま利用可能
        string? recovered = ArtifactRecovery.PreserveEmf(emfPath, warn: warn);
        if (recovered == null)
            scope.PreserveOnDispose(); // 退避に失敗したので一時 EMF を消さずに残す
        return recovered;
    }

    /// <summary>
    /// 後始末（<see cref="ArtifactScope.Dispose"/>）が済んだ後に呼ぶ。クリップボード登録が
    /// 失敗していれば回復手段を案内し、通常モードでは結果サマリを1行（＋オンのオプション行）出す。
    /// 診断モード（PETADIAG）はサマリを出さない（詳細な診断ログ側が担う）。
    /// </summary>
    /// <param name="msgs">表示文言カタログ（クリップボード失敗案内の枠と結果サマリの文言）。</param>
    /// <param name="clipboardError">登録失敗の理由。null なら登録成功。文面は下位層の原文のまま内挿する。</param>
    /// <param name="recoveredEmfPath"><see cref="RecoverIfNeeded"/> の戻り値。null なら emfPath を案内する。</param>
    public static void Report(MessageCatalog msgs,
                              TextWriter log, bool diagnostic, bool effectiveFileOutput, string emfPath,
                              string? clipboardError, string? recoveredEmfPath,
                              long emfBytes, int nodeCount,
                              bool groupByLayer, bool shxSubstitute, bool textOutline,
                              double marginMm)
    {
        if (clipboardError != null)
        {
            string available = recoveredEmfPath ?? emfPath;
            log.WriteLine(msgs.Format("outcome.clipboard_failed", clipboardError));
            log.WriteLine(msgs.Format("outcome.available_emf", available));
            log.WriteLine(msgs.Format("outcome.reuse_hint"));
        }
        if (!diagnostic)
            log.WriteLine(RunSummary.Build(msgs,
                effectiveFileOutput ? Path.GetFileName(emfPath) : null,
                emfBytes, nodeCount, clipboardError == null,
                groupByLayer, shxSubstitute, textOutline, marginMm));
    }
}
