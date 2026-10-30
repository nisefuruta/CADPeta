// 診断モード（PETADIAG／PETADATADIAG）共通の診断出力。変換各段のSVGダンプ・diag.jsonの
// 直列化・コンソールへの件数サマリを一箇所へ集約し、PetaRunner と SvgExportRunner の
// 両方から使う（書式のドリフト防止。ここに CAD 型は現れない）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using CadPeta.Ir;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

public static class DiagnosticsReport
{
    /// <summary>
    /// ConvertPipeline.ApplyWithDiagnostics へ渡す段ダンプシンク。各段の境界で IR を SVG 直列化し
    /// "&lt;base&gt;.stageNN.&lt;段名&gt;.svg" へ書き出す。診断は付随機能のため、書き込み拒否・
    /// 直列化失敗は理由をログして本処理（SVG/EMF/クリップボード）を続行する（握りつぶさない）。
    /// </summary>
    public static Action<int, string, IrDocument> MakeStageSink(string dir, string baseName, TextWriter log)
    {
        return (ordinal, stageName, ir) =>
        {
            var path = Path.Combine(dir, DiagnosticDump.FileName(baseName, ordinal, stageName));
            try
            {
                // 診断ダンプだけが出所メタデータ（data-*）を残す。
                // 通常の SVG 出力は既定＝除去のまま
                File.WriteAllText(path, SvgWriter.Write(ir, includeProvenance: true), TextEncodings.Utf8NoBom);
                log.WriteLine(Msg.Format("diag.dump", path));
            }
            catch (Exception ex) when (IsDumpFailure(ex))
            {
                // この段のダンプ失敗は本処理を巻き込まない。理由をログして次段・本出力へ進む
                log.WriteLine(Msg.Format("diag.dump_failed", path, ex.Message));
            }
        };
    }

    /// <summary>診断ログ（種別ごとの件数）をコンソールへ出す簡易形式（"  {種別}: {件数}件"）。
    /// 種別（診断カテゴリ）は言語不変で、行の書式だけを言語カタログから引く。</summary>
    public static void WriteSummaryLines(TextWriter log, IrDiagnostics diagnostics)
    {
        foreach (var (type, count, _) in diagnostics.Summarize())
            log.WriteLine(Msg.Format("diag.summary_line", type, count));
    }

    /// <summary>
    /// 診断ログ（種別・件数・対象）をヘッダ付きでコンソールへ出し、同じ内容を
    /// "&lt;svgPath&gt;.diag.json" へ書く。PETADIAG／PETADATADIAG 共通の診断成果物。
    /// </summary>
    public static void WriteDiagJson(TextWriter log, string svgPath, IrDiagnostics diagnostics)
    {
        var diagOut = new List<DiagJsonEntry>();
        log.WriteLine(Msg.Text("diag.header"));
        foreach (var (type, count, targets) in diagnostics.Summarize())
        {
            // diag.json へは type/count/targets を原文のまま直列化する（機械比較の成果物。
            // 翻訳するのはコンソール表示の書式だけ）
            log.WriteLine(Msg.Format("diag.count_line", type, count));
            diagOut.Add(new DiagJsonEntry { Type = type, Count = count, Targets = new List<string>(targets) });
        }
        if (diagnostics.Entries.Count == 0) log.WriteLine(Msg.Text("diag.none"));
        File.WriteAllText(svgPath + ".diag.json", PluginJson.Serialize(diagOut, indented: true), TextEncodings.Utf8NoBom);
        log.WriteLine(Msg.Format("diag.json_written", svgPath));
    }

    // diag.json の 1 項目（種別・件数・対象）。名前と順番は試験のスクリプトとの約束
    [DataContract]
    sealed class DiagJsonEntry
    {
        [DataMember(Name = "type", Order = 1)] public string Type { get; set; } = "";
        [DataMember(Name = "count", Order = 2)] public int Count { get; set; }
        [DataMember(Name = "targets", Order = 3)] public List<string> Targets { get; set; } = new();
    }

    // 診断ダンプで「その出力先は使えない／IR を直列化できない」を意味する例外の判別。
    // これ以外（OutOfMemory 等の実行系異常）は握りつぶさず上へ通す。
    // InvalidOperationException は SvgWriter が未知ノードを検出した場合（IR の制約プロファイル外）。
    static bool IsDumpFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
           or NotSupportedException or ArgumentException or InvalidOperationException;
}
