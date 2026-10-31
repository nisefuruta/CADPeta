#if CADPETA_ACAD || (CADPETA_ARES && !CADPETA_ARES2020)
using System;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CadPeta.Plugin.Shared;
#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Ribbon;
using Autodesk.Windows;
namespace CadPeta.Plugin.Acad;
#else
// ARES の .NET の API は AutoCAD と同じ形のリボンを持つ（Teigha.Windows・Teigha.Ribbon。2020・2023・2027 の参照の DLL で確かめた）
using Teigha.ApplicationServices;
using Teigha.Ribbon;
using Teigha.Windows;
namespace CadPeta.Plugin.Ares;
#endif

/// <summary>
/// リボンの「アドイン」タブ（AutoCAD と ARES 2023 以降）に CADPeta のパネル（大きいボタン PETA、小さいボタン PETAOPT）を置く。CUIx は使わず、読み込むたびに実行時に作る
/// （利用者の CUI とワークスペースに何も書かず、アンインストールすれば消える）。ワークスペースの読み込みでリボンが作り直されるので、
/// そのたびに置き直す。置く処理は何度呼んでも同じ結果になる。「アドイン」タブが無いときはシリーズ共通の代わりのタブを作る。
/// </summary>
internal static partial class GuiCommands
{
    // ボタンの画像の大きさ（ICO の中にこの大きさの画像を持つ。縮小すると 16 px がつぶれるので、大きさで選ぶ）
    const int SmallIconSize = 16;
    const int LargeIconSize = 32;

    static bool _ribbonHooked;

#if CADPETA_ACAD
    static RibbonControl? CurrentRibbon => ComponentManager.Ribbon;
#else
    static RibbonControl? CurrentRibbon => RibbonServices.RibbonPaletteSet?.RibbonControl;
#endif

    /// <summary>読み込みの時に呼ぶ（画面のある AutoCAD だけ）。リボンがまだ無ければ、できたときに置く。</summary>
    public static void InstallRibbon()
    {
        // 自動の試験のときだけ、リボンの準備の段階を記録する（置けなかったときに、どこで止まったかを確かめるため）
        PromptTrace.Write("Ribbon.Install", RibbonServices.RibbonPaletteSet != null ? "PaletteSetReady" : "WaitPaletteSet",
            CurrentRibbon != null ? "ribbon=ready" : "ribbon=null", valueIsNull: false, count: null);
        if (RibbonServices.RibbonPaletteSet != null) HookRibbon();
        else RibbonServices.RibbonPaletteSetCreated += OnRibbonPaletteSetCreated;
    }

    static void OnRibbonPaletteSetCreated(object? sender, EventArgs e) => Guarded(() =>
    {
        RibbonServices.RibbonPaletteSetCreated -= OnRibbonPaletteSetCreated;
        HookRibbon();
    });

    static void HookRibbon()
    {
        if (!_ribbonHooked)
        {
            // ワークスペースを読み込むとリボンが CUI から作り直され、実行時に置いたパネルは消える
            RibbonServices.RibbonPaletteSet.WorkspaceLoaded += (sender, args) => Guarded(PlaceRibbonPanel);
            _ribbonHooked = true;
        }
        // 起動の直後はリボンの本体がまだ無いことがある。AutoCAD の ItemInitialized は項目を 1 つ作るたびに来てタブが揃っていないので、
        // そこでは置かずに次のアイドルで置く
        if (CurrentRibbon != null) PlaceRibbonPanel();
#if CADPETA_ACAD
        else ComponentManager.ItemInitialized += OnRibbonItemInitialized;
#else
        else Application.Idle += OnRibbonIdle;
#endif
    }

#if CADPETA_ACAD
    static void OnRibbonItemInitialized(object? sender, RibbonItemEventArgs e) => Guarded(() =>
    {
        if (CurrentRibbon == null) return;
        ComponentManager.ItemInitialized -= OnRibbonItemInitialized;
        Application.Idle += OnRibbonIdle;
    });
#endif

    static void OnRibbonIdle(object? sender, EventArgs e) => Guarded(() =>
    {
        if (CurrentRibbon == null) return; // まだ無ければ次のアイドルで確かめる
        Application.Idle -= OnRibbonIdle;
        PlaceRibbonPanel();
    });

    /// <summary>パネルを「アドイン」タブ（無ければ代わりのタブ）に 1 つだけ置く。ほかのタブに残っていれば外し、空になった代わりのタブは消す。</summary>
    static void PlaceRibbonPanel()
    {
        var ribbon = CurrentRibbon;
        if (ribbon == null) return;

        var target = ribbon.Tabs.FirstOrDefault(tab => RibbonLayout.IsAddinsTab(tab.Id))
                     ?? ribbon.Tabs.FirstOrDefault(tab => tab.Id == RibbonLayout.FallbackTabId);
        if (target == null)
        {
            // 今あるタブの一覧は、作る前に読む（表示するのは、利用者のリボンにあったタブ）
            string tabs = string.Join(", ", ribbon.Tabs.Select(tab => tab.Id));
            target = new RibbonTab { Id = RibbonLayout.FallbackTabId, Title = Msg.Text("ribbon.fallback_tab") };
            ribbon.Tabs.Add(target);
            WriteToActive(Msg.Format("ribbon.no_addins_tab", tabs));
        }

        foreach (var tab in ribbon.Tabs.ToList())
        {
            if (ReferenceEquals(tab, target)) continue;
            foreach (var stale in tab.Panels.Where(panel => panel.Source?.Id == RibbonLayout.PanelId).ToList()) tab.Panels.Remove(stale);
            if (tab.Id == RibbonLayout.FallbackTabId && tab.Panels.Count == 0) ribbon.Tabs.Remove(tab);
        }

        if (!target.Panels.Any(panel => panel.Source?.Id == RibbonLayout.PanelId))
            target.Panels.Insert(RibbonLayout.InsertIndex(target.Panels.Select(panel => panel.Source?.Id).ToList()), CreateRibbonPanel());
        // 自動の試験のときだけ（CADPETA_PROMPT_TRACE があるとき）、置いたタブとリボンにあるタブの一覧を記録する
        PromptTrace.Write("Ribbon.Place", target.Id ?? "", "tabs=" + string.Join(",", ribbon.Tabs.Select(tab => tab.Id)),
            valueIsNull: false, count: target.Panels.Count(panel => panel.Source?.Id == RibbonLayout.PanelId));
    }

    /// <summary>
    /// 大きいボタン PETA と、小さいボタン「設定」（PETAOPT）。PETA の表示名はコマンド名にする（コマンド名を覚えてもらうため。利用者の決定、2026-10-28）。
    /// 設定のボタンは機能の名前を表示し、コマンド名はツールチップに出す（利用者の決定、2026-10-29）。
    /// </summary>
    static RibbonPanel CreateRibbonPanel()
    {
        var source = new RibbonPanelSource { Id = RibbonLayout.PanelId, Title = "CADPeta" };
        source.Items.Add(CommandButton(PluginDefaults.CommandName, PluginDefaults.CommandName, RibbonItemSize.Large, "Peta",
            Msg.Text("ribbon.peta_tip")));
        var small = new RibbonRowPanel();
        small.Items.Add(CommandButton(Msg.Text("ribbon.petaopt"), PluginDefaults.OptCommandName, RibbonItemSize.Standard, "PetaOpt",
            Msg.Text("ribbon.petaopt_tip")));
        source.Items.Add(small);
        return new RibbonPanel { Source = source };
    }

    static RibbonButton CommandButton(string text, string command, RibbonItemSize size, string icon, string description)
    {
        bool large = size == RibbonItemSize.Large;
        return new RibbonButton
        {
            Id = "CADPeta." + command,
            Text = text,
            ShowText = true,
            ShowImage = true,
            Size = size,
            Orientation = large ? System.Windows.Controls.Orientation.Vertical : System.Windows.Controls.Orientation.Horizontal,
            Image = IconFrame(icon, SmallIconSize),
            LargeImage = IconFrame(icon, LargeIconSize),
#if CADPETA_ACAD
            // 題名・説明と、下の欄のコマンド名を AutoCAD が表示する
            ToolTip = new RibbonToolTip { Title = text, Content = description, Command = command },
#else
            // ARES のリボンはツールチップの題名にボタンの表示名を出し、本文には ToolTip を文字列にして出す（RibbonToolTip を渡すと、
            // 本文が型の名前 Teigha.Windows.RibbonToolTip になった。ARES 2027、2026-10-29）。本文に説明とコマンド名を書く
            ToolTip = description + "\n" + Msg.Format("ribbon.tip_command", command),
#endif
            CommandHandler = new RibbonCommand(command),
        };
    }

    /// <summary>
    /// 埋め込んだ ICO（<c>CadPeta.&lt;icon&gt;.ico</c>。csproj の LogicalName と同じ）から <paramref name="size"/> px の画像を選ぶ。
    /// 埋め込みが無いのはビルドの誤りなので、黙ってアイコン無しにせず例外にする。
    /// </summary>
    static ImageSource IconFrame(string icon, int size)
    {
        string resourceName = "CadPeta." + icon + ".ico";
        using var stream = typeof(GuiCommands).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("DLL にアイコン " + resourceName + " が埋め込まれていない");
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.Where(candidate => candidate.PixelWidth == size)
                        .OrderByDescending(candidate => candidate.Format.BitsPerPixel).FirstOrDefault()
            ?? throw new InvalidOperationException("アイコン " + resourceName + " に " + size + " px の画像が無い");
        frame.Freeze();
        return frame;
    }

    /// <summary>
    /// リボンのイベントとボタンの処理の最外の捕捉。ここで捕まえないと WPF のハンドラから例外が抜け、AutoCAD ごと止まる。
    /// 例外の全文を前面の図面のコマンドラインに出す（図面が無ければデバッグの記録に書く）。
    /// </summary>
    static void Guarded(Action action)
    {
        try { action(); }
        catch (Exception ex) { WriteToActive(Msg.Format("ribbon.failed", ex)); }
    }

    static void WriteToActive(string message)
    {
        var editor = Application.DocumentManager.MdiActiveDocument?.Editor;
        if (editor != null) editor.WriteMessage("\n" + message + "\n");
        else System.Diagnostics.Trace.WriteLine("[CadPeta] " + message);
    }

    /// <summary>
    /// ボタンからコマンドを送る（送る文字列は <see cref="RibbonLayout.ButtonCommand"/>）。図面が開いていなければ何もしない。
    /// </summary>
    sealed class RibbonCommand : ICommand
    {
        readonly string _command;

        public RibbonCommand(string command) => _command = command;

#pragma warning disable CS0067 // 実行できるかどうかは変わらない（ICommand が要求するだけ）
        public event EventHandler? CanExecuteChanged;
#pragma warning restore CS0067

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => Guarded(() =>
        {
            if (Application.DocumentManager.MdiActiveDocument is not { } doc) return;
            // コマンドを実行中か（アクティブな図面の Editor で調べる）
            bool commandActive = !doc.Editor.IsQuiescent;
            // 自動の試験のときだけ、押したときの判定と先選択の件数を記録する（実行中と判定すると取り消しを送り、先選択も解除される）
            if (PromptTrace.IsEnabled)
                PromptTrace.Write("Ribbon.Execute", commandActive ? "CommandActive" : "Quiescent", _command, valueIsNull: false,
                    count: doc.Editor.SelectImplied().Value?.Count ?? 0);
            doc.SendStringToExecute(RibbonLayout.ButtonCommand(_command, commandActive), true, false, false);
        });
    }
}
#endif