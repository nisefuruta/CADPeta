using System;
using System.IO;

namespace CadPeta.UseCases;

/// <summary>
/// PETA 本体（プラグイン）の末尾処理を一箇所へ集約する。取得系統・ホスト CAD に依らない
/// 「クリップボード登録失敗時の回復判断」「利用者への案内文」「結果サマリの出力」を
/// 4プラグイン共通で単一実装にし、文言や回復手順のドリフトを防ぐ。
/// </summary>
public static class PetaOutcome
{
    /// <summary>
    /// クリップボードに登録できなかったときに、利用者が使える EMF のパスを返す。出力先に EMF を出していればそれを使い、
    /// 一時フォルダの EMF は動かさない。出していなければ一時フォルダの EMF を回復の場所へ移し、移せなければ一時フォルダごと
    /// 残す（<paramref name="work"/>.PreserveOnDispose）ので、どちらでも EMF を使える。
    /// </summary>
    public static string AvailableEmf(ArtifactScope work, string workEmfPath, string? publishedEmfPath, Action<string> warn)
    {
        if (publishedEmfPath != null)
            return publishedEmfPath;
        string? recovered = ArtifactRecovery.PreserveEmf(workEmfPath, warn: warn);
        if (recovered != null)
            return recovered;
        work.PreserveOnDispose(); // 移せなかったので一時の EMF を消さずに残す
        return workEmfPath;
    }

    /// <summary>
    /// 後始末（<see cref="ArtifactScope.Dispose"/>）が済んだ後に呼ぶ。写せなかったファイルとクリップボードの失敗は必ず表示し、
    /// 通常は結果の要約を、診断（PETADIAG）は出力したファイルだけを出す（詳しいことは診断のログが表示する）。
    /// </summary>
    /// <param name="clipboardError">登録失敗の理由。null なら登録成功。文面は下位層の原文のまま内挿する。</param>
    /// <param name="availableEmf">登録に失敗したときに案内する EMF（<see cref="AvailableEmf"/> の戻り値）。</param>
    public static void Report(MessageCatalog msgs,
                              TextWriter log, bool diagnostic, PublishResult publish,
                              string? clipboardError, string availableEmf,
                              long emfBytes, int nodeCount,
                              bool groupByLayer, bool shxSubstitute, bool textOutline,
                              double marginMm)
    {
        WriteFailures(log, publish);
        if (clipboardError != null)
        {
            log.WriteLine(msgs.Format("outcome.clipboard_failed", clipboardError));
            log.WriteLine(msgs.Format("outcome.available_emf", availableEmf));
            log.WriteLine(msgs.Format("outcome.reuse_hint"));
        }
        if (!diagnostic)
            log.WriteLine(RunSummary.Build(msgs, publish.Published,
                emfBytes, nodeCount, clipboardError == null,
                groupByLayer, shxSubstitute, textOutline, marginMm));
        else
            WritePublished(msgs, log, publish);
    }

    /// <summary>成果物を作らずに中止した診断（PETADIAG）で、写せなかったファイルと写したファイルを出す。</summary>
    public static void ReportPublished(MessageCatalog msgs, TextWriter log, PublishResult publish)
    {
        WriteFailures(log, publish);
        WritePublished(msgs, log, publish);
    }

    // 写せなかったファイルの理由（1 件 1 行）
    static void WriteFailures(TextWriter log, PublishResult publish)
    {
        foreach (string failure in publish.Failures)
            log.WriteLine(failure);
    }

    // 出力したファイルの名前とフォルダの 1 行（1 つも出していなければ何も出さない）
    static void WritePublished(MessageCatalog msgs, TextWriter log, PublishResult publish)
    {
        if (publish.Published.Count > 0)
            log.WriteLine(RunSummary.Published(msgs, publish.Published));
    }
}