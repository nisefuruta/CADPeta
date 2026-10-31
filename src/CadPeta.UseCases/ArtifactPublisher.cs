using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CadPeta.UseCases;

/// <summary>
/// 写したファイル（出力先の完全なパス）と、写せなかったファイルの理由（利用者に見せる文）。<paramref name="Formats"/> は、
/// 写したファイルのうち形式の成果物（診断のファイルを除く）の形式とパス。
/// </summary>
public sealed record PublishResult(IReadOnlyList<string> Published, IReadOnlyList<string> Failures,
                                  IReadOnlyDictionary<ArtifactKind, string>? Formats = null)
{
    /// <summary><paramref name="kind"/> の形式の成果物を写したパス（写していなければ null）。</summary>
    public string? PathOf(ArtifactKind kind) => Formats != null && Formats.TryGetValue(kind, out var path) ? path : null;
}

/// <summary>
/// 一時フォルダで作った成果物を、出力先へ設定の名前で写す。名前はファイル名の形から作り、{seq} は出力先の既存の番号の次にし、
/// 同じ名前があれば (1)、(2) を付ける（<see cref="ArtifactScope.ReserveInDir"/>）。写せなかったファイルは失敗の一覧に入れて、
/// ほかのファイルは写す。
/// </summary>
public static class ArtifactPublisher
{
    /// <param name="work">成果物を作った一時フォルダの予約。成果物の名前は <c>work.BaseName</c> で始まる。</param>
    /// <param name="formats">写す形式。</param>
    /// <param name="diagnostics">true なら形式にかかわらず、一時フォルダの <c>work.BaseName.</c> で始まるファイルをすべて写す（PETADIAG）。</param>
    public static PublishResult Publish(ArtifactScope work, ArtifactKindSet formats, bool diagnostics,
                                        FileNameTemplate template, FileNameFields fields, string outputDir,
                                        Action<string>? warn = null)
    {
        var catalog = MessageCatalogs.Current;
        // 写すファイル: 一時フォルダのパスと、名前の work.BaseName より後の部分（.emf、.svg.diag.json など）と、検査する形式
        var sources = formats.Kinds.Select(kind => (Path: work.PathOf(kind), Kind: (ArtifactKind?)kind)).ToList();
        if (diagnostics)
            sources = Directory.EnumerateFiles(work.Dir, work.BaseName + ".*")
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path => (Path: path, Kind: KindOf(work, path)))
                .ToList();
        if (sources.Count == 0)
            return new PublishResult(Array.Empty<string>(), Array.Empty<string>());

        var published = new List<string>();
        var byKind = new Dictionary<ArtifactKind, string>();
        var failures = new List<string>();
        try
        {
            string[] existing = Directory.Exists(outputDir)
                ? Directory.EnumerateFiles(outputDir).Select(Path.GetFileName).ToArray()!
                : Array.Empty<string>();
            string name = template.Expand(fields, template.NextSeq(fields, existing));
            using var scope = ArtifactScope.ReserveInDir(outputDir, name, warn);
            foreach (var (source, kind) in sources)
            {
                string destination = Path.Combine(scope.Dir, scope.BaseName + Path.GetFileName(source).Substring(work.BaseName.Length));
                try
                {
                    if (kind is { } k)
                        ArtifactFile.WriteAtomically(destination, k, temp => File.Copy(source, temp, overwrite: true));
                    else
                        File.Copy(source, destination);
                    published.Add(destination);
                    if (kind is { } written)
                        byKind[written] = destination;
                }
                catch (Exception ex) when (ArtifactFile.IsWriteFailure(ex))
                {
                    failures.Add(catalog.Format("publish.copy_failed", destination, FileFailures.OneLine(ex.Message)));
                }
            }
        }
        catch (Exception ex) when (ArtifactFile.IsWriteFailure(ex))
        {
            failures.Add(catalog.Format("publish.folder_failed", outputDir, FileFailures.OneLine(ex.Message)));
        }
        return new PublishResult(published, failures, byKind);
    }

    // 一時フォルダの成果物の形式（診断のファイルなど、形式の成果物でなければ null。null は中身を検査せずに写す）
    static ArtifactKind? KindOf(ArtifactScope work, string path) =>
        ArtifactKinds.All.Where(kind => string.Equals(work.PathOf(kind), path, StringComparison.OrdinalIgnoreCase))
            .Select(kind => (ArtifactKind?)kind).FirstOrDefault();
}
