using CadPeta.Plugin.Shared;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

/// <summary>
/// 言語カタログ（1041.lang/1033.lang）と言語解決（Msg）の回帰。ja カタログが従来文言と
/// 一致することは RangePromptTests・RunSummaryTests 等の既存テスト（既定＝ja）が固定する。
/// ここではキーの網羅・言語ファミリ判定・en フォールバック・lang 形式のパーサを固定する。
/// </summary>
public sealed class MsgCatalogTests
{
    [Fact]
    public void 日英カタログはキー集合が一致する()
    {
        var ja = Msg.Japanese.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        var en = Msg.English.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(ja);
        Assert.Equal(en, ja);
    }

    [Theory]
    [InlineData("ja", "1041")]
    [InlineData("JA", "1041")] // AutoCAD 2026 日本語版の LOCALE 実値（大文字）
    [InlineData("ja-JP", "1041")]
    [InlineData("ja_JP", "1041")]
    [InlineData("JPN", "1041")]
    [InlineData("jp", "1041")]
    [InlineData("en", "1033")]
    [InlineData("en-GB", "1033")]
    [InlineData("enu", "1033")]
    [InlineData(@"Software\Autodesk\AutoCAD\R25.1\ACAD-9101:411", "1041")]
    [InlineData(@"Software\Autodesk\AutoCAD\R25.1\ACAD-9101:409", "1033")]
    [InlineData(@"ACAD-9101:809", "1033")] // en-GB のプライマリ言語 ID も en 扱い
    public void 言語ファミリとレジストリLCIDをカタログへ写像する(string hint, string expected)
    {
        Assert.Equal(expected, Msg.TryMapToLcid(hint));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("fr-FR")]
    [InlineData("de-DE")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(@"Software\Vendor\Cad\V1")] // LCID 無しのパスは判定材料にしない
    [InlineData(@"ACAD-9101:40C")]          // fr のプライマリ言語 ID
    public void 未対応言語や判定不能ヒントは写像しない(string? hint)
    {
        Assert.Null(Msg.TryMapToLcid(hint));
    }

    [Fact]
    public void 未対応言語だけのヒント列はenへフォールバックし判定不能を返す()
    {
        string lcid = Msg.ChooseLcid(new string?[] { "fr-FR", null, "de" }, out bool resolved);
        Assert.Equal(Msg.EnglishLcid, lcid);
        Assert.False(resolved);
    }

    [Fact]
    public void 最初に写像できたヒントが勝つ()
    {
        string lcid = Msg.ChooseLcid(new string?[] { null, "fr", "ja", "en" }, out bool resolved);
        Assert.Equal(Msg.JapaneseLcid, lcid);
        Assert.True(resolved);
    }

    [Fact]
    public void キー欠落はenで補い両方に無ければキー名を可視化する()
    {
        var en = new MessageCatalog("1033", new Dictionary<string, string> { ["a"] = "A!" });
        var ja = new MessageCatalog("1041", new Dictionary<string, string>(), en);
        Assert.Equal("A!", ja.Format("a"));                  // en フォールバック
        Assert.Equal("[b]", ja.Format("b"));                 // どこにも無い → 黙って空にしない
    }

    [Fact]
    public void 値は引数の有無に依らず複合書式として展開する()
    {
        var catalog = new MessageCatalog("1033", new Dictionary<string, string>
        {
            ["braces"] = "a{{b}}c",
        });
        Assert.Equal("a{b}c", catalog.Format("braces")); // 引数なしでも {{ は { として表示する
    }

    [Fact]
    public void 型依存の書式不正は表示時に次の段の文言で受け止め通知する()
    {
        // {0:Q} は文字列プローブ（読込時検証）を通過するが、実引数が int だと
        // FormatException になる。連鎖の次（組み込み相当）の文言で表示を続け、
        // 失敗した段（カタログ名・キー・理由）を通知する（黙って切り替えない）
        var inner = new MessageCatalog("1033-builtin", new Dictionary<string, string> { ["k"] = "ok {0}" });
        var outer = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" }, inner);
        var notified = new List<string>();
        using (MessageCatalog.BeginFormatFallbackScope((name, key, error) => notified.Add($"{name}/{key}")))
            Assert.Equal("ok 3", outer.Format("k", 3));
        Assert.Equal(new[] { "1033/k" }, notified);
        Assert.Equal("[k]", new MessageCatalog("1033",
            new Dictionary<string, string> { ["k"] = "broken {0:Q}" }).Format("k", 3)); // 最後まで失敗なら可視化
    }

    [Fact]
    public void 通知文言の組み立て失敗による再帰は止まる()
    {
        var catalog = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" });
        int calls = 0;
        // 通知先が再び壊れたキーを組み立てる最悪ケースでも、再帰通知はガードで止まる
        using var scope = MessageCatalog.BeginFormatFallbackScope(
            (_, _, _) => { calls++; _ = catalog.Format("k", 3); });
        Assert.Equal("[k]", catalog.Format("k", 3));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void 通知先の例外はフォールバック継続を妨げない()
    {
        // 診断出力（通知）の失敗で、防いだはずのコマンド中断を再導入しない。
        // 通知先がログ書き込み不能等で例外を投げても、組み込み文言の表示は続く
        var inner = new MessageCatalog("1033-builtin", new Dictionary<string, string> { ["k"] = "ok {0}" });
        var outer = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" }, inner);
        using var scope = MessageCatalog.BeginFormatFallbackScope(
            (_, _, _) => throw new IOException("log write failed"));
        Assert.Equal("ok 3", outer.Format("k", 3));
    }

    [Fact]
    public void 通知の再帰ガードは他スレッドの通知を失わない()
    {
        // ガードがインスタンス共有だと、通知中の別スレッドの失敗まで再帰と誤判定して
        // 無通知になる。ガードはスレッド単位であること
        var inner = new MessageCatalog("1033-builtin", new Dictionary<string, string> { ["k"] = "ok {0}" });
        var main = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" }, inner);
        var other = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" }, inner);
        int notifications = 0;
        using var scope = MessageCatalog.BeginFormatFallbackScope((_, _, _) =>
        {
            // 1回目（メインスレッドの通知中）に、別スレッドで他の失敗を起こす。
            // 通知先は実行コンテキストで子スレッドへも引き継がれ、ガードがスレッド単位
            // なので子スレッドの通知（2回目）は失われない。
            // Task.Run(...).Wait() は待機側スレッドへのインライン実行があり得るため、
            // 必ず別スレッドで実行する専用 Thread を使う
            if (Interlocked.Increment(ref notifications) == 1)
            {
                var thread = new Thread(() => other.Format("k", 3));
                thread.Start();
                thread.Join();
            }
        });
        Assert.Equal("ok 3", main.Format("k", 3));
        Assert.Equal(2, notifications);
    }

    sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("log unavailable");
    }

    [Fact]
    public void 初期報告の失敗時は言語文脈を直前の状態へ戻してから再送出する()
    {
        // 設定後の初期報告（log.WriteLine）が失敗すると呼び出し側はスコープを受け取れず
        // 解除できない。Initialize 側で直前の状態へ戻してから再送出すること
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // キー欠落＋型依存の書式不正を持つ差し替えファイルで、初期報告（lang.missing_keys）を
            // 必ず発生させる
            File.WriteAllText(Path.Combine(dir, "1033.lang"), "confirm.partial=broken {0:Q}\n");
            var slot = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);
            int outerNotified = 0;
            using (MessageCatalog.BeginFormatFallbackScope((_, _, _) => outerNotified++))
            {
                Assert.Throws<IOException>(() => Msg.InitializeSlot(slot, resolved: true, new ThrowingWriter()));
                // InitializeSlot が設定した通知先は解除済みで、外側（直前）の通知先へ戻っている
                _ = slot.Catalog.Format("confirm.partial", 3);
                Assert.Equal(1, outerNotified);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 通知先はスコープ終了で直前の状態へ戻り解除後は呼ばれない()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "1033.lang"), "confirm.partial=broken {0:Q}\n");
            var slot = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);

            // 解除後は通知されない（コマンド終了後のログ・Editor 参照へ遅延書き込みしない）
            var first = new StringWriter();
            var scope1 = Msg.AttachFormatFallbackObserver(slot, first);
            scope1.Dispose();
            scope1.Dispose(); // 多重 Dispose も安全（二重復元しない）
            _ = slot.Catalog.Format("confirm.partial", 3);
            Assert.Equal("", first.ToString());

            // 入れ子は内側だけへ届き、内側の終了で外側へ戻る
            var outerLog = new StringWriter();
            using (Msg.AttachFormatFallbackObserver(slot, outerLog))
            {
                var innerLog = new StringWriter();
                using (Msg.AttachFormatFallbackObserver(slot, innerLog))
                    _ = slot.Catalog.Format("confirm.partial", 3);
                Assert.Contains("confirm.partial", innerLog.ToString());
                Assert.Equal("", outerLog.ToString()); // 独立した文脈の診断を相互配送しない
                _ = slot.Catalog.Format("confirm.partial", 3);
                Assert.Contains("confirm.partial", outerLog.ToString());
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 独立したスレッドの文脈間で通知を相互配送しない()
    {
        var inner = new MessageCatalog("1033-builtin", new Dictionary<string, string> { ["k"] = "ok {0}" });
        var catalog = new MessageCatalog("1033", new Dictionary<string, string> { ["k"] = "broken {0:Q}" }, inner);
        int mainNotified = 0, threadNotified = 0;
        using (MessageCatalog.BeginFormatFallbackScope((_, _, _) => Interlocked.Increment(ref mainNotified)))
        {
            var thread = new Thread(() =>
            {
                // 子スレッドが自分の文脈で通知先を設定しても、親の文脈には影響しない
                using (MessageCatalog.BeginFormatFallbackScope((_, _, _) => Interlocked.Increment(ref threadNotified)))
                {
                    _ = catalog.Format("k", 3);
                    _ = catalog.Format("k", 3);
                }
            });
            thread.Start();
            thread.Join();
            _ = catalog.Format("k", 3);
        }
        Assert.Equal(2, threadNotified); // 別文脈の2回は別文脈だけへ
        Assert.Equal(1, mainNotified);   // 自文脈の1回だけ（相手の2回が混ざらない）
    }

    [Fact]
    public void 言語選択は実行コンテキスト単位で親の文脈を汚さない()
    {
        Assert.Equal(Msg.JapaneseLcid, Msg.Current.Name); // 未初期化の既定は ja
        string? inScope = null, afterDispose = null;
        var thread = new Thread(() =>
        {
            using (Msg.Initialize(new[] { "fr" }, new StringWriter()))
                inScope = Msg.Current.Name;
            afterDispose = Msg.Current.Name;
        });
        thread.Start();
        thread.Join();
        Assert.Equal(Msg.EnglishLcid, inScope);       // fr → en フォールバックが子の文脈で効く
        Assert.Equal(Msg.JapaneseLcid, afterDispose); // スコープ終了で直前の選択（既定 ja）へ戻る
        Assert.Equal(Msg.JapaneseLcid, Msg.Current.Name); // 親の文脈は汚れない
    }

    [Fact]
    public void パーサはコメントと空行を無視しエスケープを解決する()
    {
        var map = MessageCatalog.Parse(
            "# comment\r\n\r\nkey.a=v1\nkey.b=\\n行頭改行と行末空白\\s\nkey.c=a=b\nkey.d=\\t\\\\\n");
        Assert.Equal("v1", map["key.a"]);
        Assert.Equal("\n行頭改行と行末空白 ", map["key.b"]);
        Assert.Equal("a=b", map["key.c"]); // 値の = は最初の = 以降を素通しする
        Assert.Equal("\t\\", map["key.d"]);
    }

    [Theory]
    [InlineData("broken line")]         // = が無い
    [InlineData("key.a=1\nkey.a=2")]    // 重複キー
    [InlineData("key.a=bad\\z")]        // 未知のエスケープ
    [InlineData("key.a=閉じない{0")]    // 不正な複合書式（実行中でなく読込時に検出する）
    [InlineData("key.a=空の指定{}")]    // 不正な複合書式
    [InlineData("key.a=単独の{")]       // 不正な複合書式（表示規則と同じ扱いで読込時に弾く）
    public void パーサは不正な内容を黙って捨てずFormatExceptionにする(string content)
    {
        Assert.Throws<FormatException>(() => MessageCatalog.Parse(content));
    }

    [Fact]
    public void 差し替えファイルのキー欠落は組み込みの文言で補い欠落として検出される()
    {
        // 外部 lang ファイルの差し替え時: 欠落キーは同言語の組み込みカタログで補い、
        // MissingKeysComparedToFallback が欠落を検出する（en 選択時も成立すること）
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "1033.lang"), "range.cancelled=custom cancel\n");
            var en = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);
            Assert.False(en.FromEmbedded);
            Assert.Equal("custom cancel", en.Catalog.Format("range.cancelled"));
            Assert.Contains("data.aborted", en.MissingKeys);
            Assert.Equal("PETADATA aborted", en.Catalog.Format("data.aborted")); // 組み込み en で補完

            File.WriteAllText(Path.Combine(dir, "1041.lang"), "range.cancelled=独自の中止文言\n");
            var ja = Msg.LoadFromDir(dir, Msg.JapaneseLcid, fallback: en.Catalog);
            Assert.Equal("独自の中止文言", ja.Catalog.Format("range.cancelled"));
            Assert.Contains("data.aborted", ja.MissingKeys);
            Assert.Equal("PETADATAを中止した", ja.Catalog.Format("data.aborted")); // 組み込み ja で補完
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Theory]
    [InlineData("runner.svg_written=bad {0")]  // 閉じていない中括弧（構造の不正）
    [InlineData("runner.svg_written=bad {1}")] // 組み込み定義（{0} のみ）に無い引数番号
    public void 不正な複合書式の差し替えファイルは読込時に組み込みへ切り替える(string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "1033.lang"), content + "\n");
            var slot = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);
            Assert.True(slot.FromEmbedded);   // ファイル不読として組み込みで続行
            Assert.NotNull(slot.FileError);   // 理由は Initialize が lang.file_invalid で報告する
            Assert.Equal("SVG output: x", slot.Catalog.Format("runner.svg_written", "x"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 型依存の書式不正の差し替え値は表示時に組み込みの文言へ切り替える()
    {
        // {0:Q} は読込時の検証（文字列プローブ・引数番号の検査）を通過するが、
        // 実引数の int では FormatException になる。表示は組み込みの文言で続ける
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "1033.lang"), "confirm.partial=broken {0:Q}\n");
            var slot = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);
            Assert.False(slot.FromEmbedded);  // 読込は通る（型までは読込時に判定できない）
            var report = new StringWriter();
            Msg.AttachFormatFallbackObserver(slot, report);
            Assert.Equal("\nExclude 3 unsupported object(s) and output the rest? [Yes/No] <No>: ",
                slot.Catalog.Format("confirm.partial", 3));
            // 切替の都度、対象キーと理由をコマンドラインへ報告する（黙って切り替えない）
            Assert.Contains("confirm.partial", report.ToString());
            Assert.Contains("cannot be formatted", report.ToString());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void 外部enの追加キーをjaの欠落として誤報しない()
    {
        string dir = Path.Combine(Path.GetTempPath(), "CadPeta_lang_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 完全な同梱カタログ（テスト出力の lang フォルダ）を複製し、en にだけ独自キーを足す。
            // net48 の xUnit はシャドウコピーで Assembly.Location が lang を持たない場所を
            // 指すため、実配置は BaseDirectory から引く
            var shipped = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang");
            File.Copy(Path.Combine(shipped, "1041.lang"), Path.Combine(dir, "1041.lang"));
            File.WriteAllText(Path.Combine(dir, "1033.lang"),
                File.ReadAllText(Path.Combine(shipped, "1033.lang")) + "custom.extra=hello\n");

            var en = Msg.LoadFromDir(dir, Msg.EnglishLcid, fallback: null);
            var ja = Msg.LoadFromDir(dir, Msg.JapaneseLcid, fallback: en.Catalog);
            Assert.Empty(en.MissingKeys);
            Assert.Empty(ja.MissingKeys);     // en の追加キーは ja の欠落ではない
            Assert.Equal("hello", en.Catalog.Format("custom.extra"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void jaカタログのプロンプト文言は従来と同一の組み立てになる()
    {
        // E2E・単体テストが固定している代表文言をカタログ経由で再構成できることの回帰
        Assert.Equal("\n最初のコーナーを指定 または [全体(A)/オブジェクト(O)] <全体(A)>",
            Msg.Japanese.Format("range.first_corner", "全体(A)/オブジェクト(O)", "全体(A)"));
        Assert.Equal("\nもう一方のコーナーを指定: ", Msg.Japanese.Format("range.second_corner"));
        Assert.Equal("\n3件の非対応対象を除外して部分出力する? [Yes/No] <No>: ",
            Msg.Japanese.Format("confirm.partial", 3));
        Assert.Equal("絞り込み: 選択5件中4件を出力対象にする",
            Msg.Japanese.Format("selplot.session_summary", 5, 4));
        Assert.Equal("ペーパー空間の制御ビューポートは選択出力の対象にできない",
            Msg.Japanese.Format("selplot.reason.viewport_controller"));
        Assert.Equal("オフのビューポートは表示内容がないため選択出力の対象にできない",
            Msg.Japanese.Format("selplot.reason.viewport_off"));
        Assert.Equal("CADPETA_FORCE_SELECTION_CLONE_FALLBACK=1 のため Wblock を使わずオブジェクト複製で側DBを作る",
            Msg.Japanese.Format("selplot.force_clone", "CADPETA_FORCE_SELECTION_CLONE_FALLBACK"));
        Assert.Equal("側DBの CTB/STB モードが元図面と一致しない",
            Msg.Japanese.Format("selplot.stb_mismatch"));
        Assert.Equal("レイアウト: PETA_A3（デバイス=Dev.pc3、用紙=A3、スタイル=なし）",
            Msg.Japanese.Format("plotport.layout_info", "PETA_A3", "Dev.pc3", "A3",
                Msg.Japanese.Format("plotport.style_none")));
        Assert.Equal("用紙 A3 は Dev.pc3 に存在せず、寸法の合う用紙も無いため既定用紙で進める（出力寸法が変わる）",
            Msg.Japanese.Format("plotport.media_missing", "A3", "Dev.pc3"));
        Assert.Equal("用紙 A3 は発行デバイスに無いため、寸法の合う ISO_A3 を使う（297.01×419.99mm）",
            Msg.Japanese.Format("plotport.media_matched_by_size", "A3", "ISO_A3", 297.010651, 419.988983));
        Assert.Equal("デバイス上書きで用紙単位が Inches に変わったため、ページ設定の Millimeters へ戻す",
            Msg.Japanese.Format("plotport.paper_units_restored", "Inches", "Millimeters"));
        Assert.Equal("ビューポート内のため、窓指定の間ペーパー空間へ切り替える（窓はペーパー座標）",
            Msg.Japanese.Format("editor.paper_pick_switch"));
        Assert.Equal("クリップボード登録: CF_ENHMETAFILE 15,204 bytes（読み戻しバイト一致を確認）。Office へ Ctrl+V で貼り付けられる",
            Msg.Japanese.Format("emf.registered", 15204));
        Assert.Equal("警告: 一時成果物を削除できない: C:\\x（使用中）",
            Msg.Japanese.Format("artifact.warn_frame",
                Msg.Japanese.Format("artifact.temp_delete_failed", "C:\\x"), "使用中"));
        Assert.Equal("[CadPeta] PETA 読込 stamp",
            Msg.Japanese.Format("app.loaded", "PETA", "stamp"));
        Assert.Equal("構造SVG書き出し取得: 174エンティティ → 3065ノード 範囲 400.00x280.00mm（尺度 1:1・余白 0mm）",
            Msg.Japanese.Format("acquire.data_result", 174, 3065, 400.0, 280.0, 1.0, 0.0));
        Assert.Equal("構造SVG書き出し取得: 写像できた図形がない（入力 5 エンティティ）",
            Msg.Japanese.Format("acquire.data_empty", 5));
    }

    [Fact]
    public void 未初期化の文脈のカタログ既定はjaでビルド識別子も従来文言になる()
    {
        Assert.Same(MessageCatalogs.Japanese, MessageCatalogs.Current);
        // BuildStamp は ambient カタログを参照する。未初期化＝ja で従来文言（BuildStampTests が固定）
        Assert.Contains("読み込み元:", BuildStamp.Format(null, null, null, @"C:\x.dll", true));
        // en の言語スコープ内では英語になる
        using (MessageCatalogs.BeginLanguageScope(MessageCatalogs.English))
        {
            var text = BuildStamp.Format(null, null, null, @"C:\x.dll", true);
            Assert.Contains("Loaded from:", text);
            Assert.Contains("(version unknown)", text);
            Assert.DoesNotContain("読み込み元:", text);
        }
        Assert.Same(MessageCatalogs.Japanese, MessageCatalogs.Current);
    }
}
