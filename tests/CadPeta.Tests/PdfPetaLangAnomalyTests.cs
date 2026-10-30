#if NET10_0_OR_GREATER
using System;
using System.IO;
using CadPeta.App;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

/// <summary>
/// CAD 不要の合成ルート（CLI・GUI が使う PdfPetaPipeline）の言語カタログ異常報告の回帰。
/// 文言の言語切替は対象外（常に既定 ja）のまま、差し替え lang ファイルの欠陥
/// （読込不能・キー欠落・表示時の書式不正）だけを実行ログへ報告する（CAD 殻の
/// Msg 初期化に相当する安全網。MsgCatalogTests が固定する通知機構そのものの検査と対になる）。
/// </summary>
public sealed class PdfPetaLangAnomalyTests
{
    static MessageCatalogSlot LoadJaSlot(string dir) =>
        MessageCatalogs.LoadFromDir(dir, MessageCatalogs.JapaneseLcid, fallback: null);

    // 各試験が finally で削除する
    static string NewTempDir() => new TempFolder().Path;

    [Fact]
    public void 読めない差し替えファイルは理由付きで報告する()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "1041.lang"), "壊れた行\n");
            var slot = LoadJaSlot(dir);
            Assert.NotNull(slot.FileError);
            var log = new StringWriter();
            using (PdfPetaPipeline.BeginCatalogAnomalyScope(slot, log)) { }
            Assert.Contains("読めないため組み込みの文言を使う", log.ToString());
            Assert.Contains(slot.FilePath, log.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 差し替えファイルのキー欠落を件数と一覧で報告する()
    {
        string dir = NewTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "1041.lang"), "range.cancelled=独自の中止文言\n");
            var slot = LoadJaSlot(dir);
            Assert.NotEmpty(slot.MissingKeys);
            var log = new StringWriter();
            using (PdfPetaPipeline.BeginCatalogAnomalyScope(slot, log)) { }
            Assert.Contains("未定義のキー", log.ToString());
            Assert.Contains("emf.file_missing", log.ToString()); // 欠落キーの一覧に共有層のキーが載る
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void langファイルの不在は報告しない()
    {
        // 言語切替を提供しない CLI・GUI では lang 不在（単一ファイル配布を含む）は常態であり、
        // CAD 殻の初期化報告（lang.file_missing）と異なり警告を出さない
        string dir = NewTempDir();
        try
        {
            var slot = LoadJaSlot(dir);
            Assert.True(slot.FromEmbedded);
            Assert.Null(slot.FileError);
            var log = new StringWriter();
            using (PdfPetaPipeline.BeginCatalogAnomalyScope(slot, log)) { }
            Assert.Equal("", log.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 表示時フォールバックはスコープ内だけ報告し解除後は通知しない()
    {
        string dir = NewTempDir();
        try
        {
            // {0:Q} は読込時検証（文字列プローブ）を通過するが、実引数の int で FormatException
            // になる（MsgCatalogTests と同じ再現手順）
            File.WriteAllText(Path.Combine(dir, "1041.lang"), "confirm.partial=壊れた {0:Q}\n");
            var slot = LoadJaSlot(dir);
            Assert.False(slot.FromEmbedded);
            var log = new StringWriter();
            using (PdfPetaPipeline.BeginCatalogAnomalyScope(slot, log))
                _ = slot.Catalog.Format("confirm.partial", 3);
            string inScope = log.ToString();
            Assert.Contains("confirm.partial", inScope);
            Assert.Contains("組み込みの文言で続ける", inScope);

            _ = slot.Catalog.Format("confirm.partial", 3); // 解除後の失敗は遅延書き込みしない
            Assert.Equal(inScope, log.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
#endif
