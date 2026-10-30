// クリップボード出力アダプタ: 出力済み EMF ファイルを CF_ENHMETAFILE として
// クリップボードへ登録する。ファイル経由とクリップボード経由で Office 貼り付け結果が
// 変わらないことは実測済み。
// AutoCAD コマンドコンテキスト（STAメインスレッド）でも accoreconsole（ヘッドレス）でも
// 成立することを実機プローブで確認済み（OpenClipboard(NULL) の即時データ登録は
// MS Learn の遅延レンダリング時の注意に該当しない）。
//
// 所有権の規約（MS Learn SetClipboardData）:
// - SetClipboardData が成功した瞬間にハンドルはシステム所有となり、こちらから解放してはならない
// - 失敗した場合のみ所有権がこちらに残るため、DeleteEnhMetaFile で解放する
using System;
using System.IO;
using System.Threading;
using System.Diagnostics;
using CadPeta.UseCases;

namespace CadPeta.Out.Emf;

public static class EmfClipboard
{
    /// <summary>
    /// OpenClipboard の競合リトライ回数と間隔。他プロセス（Office・クリップボード履歴等）が
    /// 一時的に Open している間は失敗するため有限回リトライする。値は .NET WinForms
    /// Clipboard.SetDataObject の既定（retryTimes=10, retryDelay=100ms）と同値に揃える。
    /// </summary>
    private const int OpenClipboardRetryCount = 10;
    private const int OpenClipboardRetryIntervalMs = 100;

    /// <summary>
    /// 出力済み EMF ファイルをクリップボードへ CF_ENHMETAFILE として登録し、
    /// 読み戻しバイト比較の自己検証まで行う。失敗は全経路で文脈付き例外を上げる
    /// （呼び出し元＝合成ルートのコマンド境界が利用者向けに報告する）。
    /// </summary>
    public static void SetFromFile(string emfPath, TextWriter log, TextWriter? verbose = null)
        => SetFromFile(emfPath, log, verbose, Win32ClipboardNative.Instance,
                       Thread.Sleep, OpenClipboardRetryCount, OpenClipboardRetryIntervalMs);

    internal static void SetFromFile(string emfPath, TextWriter log, TextWriter? verbose,
                                     IEmfClipboardNative native, Action<int> sleep,
                                     int retryCount, int retryIntervalMs)
    {
        // 表示文言は実行コンテキストの言語カタログから引く（未初期化の文脈＝CLI 等は ja）
        var msgs = MessageCatalogs.Current;
        // 前提確認: 出力段の成功報告を鵜呑みにせず、成果物の実在から確認する
        var info = new FileInfo(emfPath);
        if (!info.Exists)
            throw new InvalidOperationException(msgs.Format("emf.file_missing", emfPath));
        if (info.Length == 0)
            throw new InvalidOperationException(msgs.Format("emf.file_empty", emfPath));
        byte[] fileBytes = File.ReadAllBytes(emfPath);

        // ファイル→メモリハンドル。クリップボードへはシステムに所有権を渡せる複製を作って渡す
        IntPtr hemf = native.GetEnhMetaFile(info.FullName);
        if (hemf == IntPtr.Zero)
            throw new InvalidOperationException(msgs.Format("emf.load_failed", native.LastError, emfPath));
        IntPtr hCopy;
        try
        {
            hCopy = native.CopyEnhMetaFile(hemf);
            if (hCopy == IntPtr.Zero)
                throw new InvalidOperationException(msgs.Format("emf.copy_failed", native.LastError));
        }
        finally
        {
            native.DeleteEnhMetaFile(hemf);
        }

        // 登録。成功した瞬間に hCopy はシステム所有となる（以降こちらから触らない）
        bool copyOwned = true;
        try
        {
            OpenClipboardWithRetry(msgs, "emf.stage.set", native, sleep, retryCount, retryIntervalMs);
            try
            {
                if (!native.EmptyClipboard())
                    throw new InvalidOperationException(msgs.Format("emf.empty_failed", native.LastError));
                if (native.SetClipboardData(hCopy) == IntPtr.Zero)
                    throw new InvalidOperationException(msgs.Format("emf.set_failed", native.LastError));
                copyOwned = false;
            }
            finally
            {
                if (!native.CloseClipboard())
                {
                    // Open したまま放置すると全プロセスのクリップボードを塞ぐため、失敗は必ず報告する。
                    // ただし登録自体の成否とは独立のため例外にはせず（finally からの throw は本例外を
                    // 潰す）、警告として残す
                    log.WriteLine(msgs.Format("emf.close_failed", native.LastError));
                }
            }
        }
        finally
        {
            if (copyOwned)
                native.DeleteEnhMetaFile(hCopy);
        }

        // 自己検証: 登録済みデータを読み戻してバイト一致を確認する（クリップボードは
        // マシン全域の共有状態で単体テストに向かないため、実行のたびに検証する）
        VerifyReadBack(msgs, fileBytes, log, verbose ?? log, native, sleep,
                       retryCount, retryIntervalMs);
    }

    /// <summary>登録済み CF_ENHMETAFILE を読み戻し、元ファイルとのバイト一致を確認する。</summary>
    private static void VerifyReadBack(MessageCatalog msgs, byte[] fileBytes, TextWriter log,
                                       TextWriter verbose, IEmfClipboardNative native,
                                       Action<int> sleep, int retryCount, int retryIntervalMs)
    {
        OpenClipboardWithRetry(msgs, "emf.stage.verify", native, sleep, retryCount, retryIntervalMs);
        try
        {
            IntPtr hGot = native.GetClipboardData();
            if (hGot == IntPtr.Zero)
                throw new InvalidOperationException(msgs.Format("emf.readback_missing", native.LastError));
            uint size = native.GetEnhMetaFileBits(hGot, 0, null);
            if (size == 0)
                throw new InvalidOperationException(msgs.Format("emf.readback_bits_failed", native.LastError));
            var got = new byte[size];
            uint written = native.GetEnhMetaFileBits(hGot, size, got);
            // 取得ハンドルはクリップボード所有。DeleteEnhMetaFile してはならない
            if (written != size || !BytesEqual(got, fileBytes))
                throw new InvalidOperationException(
                    msgs.Format("emf.readback_mismatch", written, fileBytes.Length));
        }
        finally
        {
            if (!native.CloseClipboard())
                log.WriteLine(msgs.Format("emf.close_failed", native.LastError));
        }
        verbose.WriteLine(msgs.Format("emf.registered", fileBytes.Length));
    }

    /// <summary>
    /// OpenClipboard を有限回リトライする。競合（他プロセスが Open 中）が尽きたら、
    /// 保持プロセスを特定して文脈付きで上げる。<paramref name="stageKey"/> は段名の
    /// カタログキー（emf.stage.set / emf.stage.verify）。
    /// </summary>
    private static void OpenClipboardWithRetry(MessageCatalog msgs, string stageKey,
                                               IEmfClipboardNative native, Action<int> sleep,
                                               int retryCount, int retryIntervalMs)
    {
        for (int attempt = 1; ; attempt++)
        {
            // NULL ウィンドウでの Open は即時データの登録に支障がないことを実機プローブで
            // 確認済み（GUI 専用層 acmgd への依存を避け、accoreconsole 互換を保つ）
            if (native.OpenClipboard())
                return;
            if (attempt >= retryCount)
                throw new InvalidOperationException(msgs.Format("emf.open_failed",
                    msgs.Format(stageKey), native.LastError, retryCount,
                    retryIntervalMs, DescribeHolder(msgs, native)));
            sleep(retryIntervalMs);
        }
    }

    /// <summary>クリップボードを Open したままにしているプロセスの説明（診断用）。</summary>
    private static string DescribeHolder(MessageCatalog msgs, IEmfClipboardNative native)
    {
        IntPtr holder = native.GetOpenClipboardWindow();
        if (holder == IntPtr.Zero)
            return msgs.Format("emf.holder_unknown");
        try
        {
            native.GetWindowThreadProcessId(holder, out uint pid);
            using var p = Process.GetProcessById((int)pid);
            return $"{p.ProcessName} (PID {pid})";
        }
        catch (System.Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // プロセスが直後に終了した等。診断情報が減るだけで登録失敗の報告自体は成立する
            return msgs.Format("emf.holder_gone");
        }
    }

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length)
            return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }

}

internal interface IEmfClipboardNative
{
    int LastError { get; }
    IntPtr GetEnhMetaFile(string path);
    IntPtr CopyEnhMetaFile(IntPtr source);
    bool DeleteEnhMetaFile(IntPtr handle);
    bool OpenClipboard();
    bool EmptyClipboard();
    IntPtr SetClipboardData(IntPtr handle);
    bool CloseClipboard();
    IntPtr GetClipboardData();
    uint GetEnhMetaFileBits(IntPtr handle, uint size, byte[]? buffer);
    IntPtr GetOpenClipboardWindow();
    uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}

internal sealed class Win32ClipboardNative : IEmfClipboardNative
{
    internal static readonly Win32ClipboardNative Instance = new();
    public int LastError => System.Runtime.InteropServices.Marshal.GetLastWin32Error();
    public IntPtr GetEnhMetaFile(string path) => Gdi.GetEnhMetaFileW(path);
    public IntPtr CopyEnhMetaFile(IntPtr source) => Gdi.CopyEnhMetaFileW(source, null);
    public bool DeleteEnhMetaFile(IntPtr handle) => Gdi.DeleteEnhMetaFile(handle);
    public bool OpenClipboard() => Gdi.OpenClipboard(IntPtr.Zero);
    public bool EmptyClipboard() => Gdi.EmptyClipboard();
    public IntPtr SetClipboardData(IntPtr handle) => Gdi.SetClipboardData(Gdi.CF_ENHMETAFILE, handle);
    public bool CloseClipboard() => Gdi.CloseClipboard();
    public IntPtr GetClipboardData() => Gdi.GetClipboardData(Gdi.CF_ENHMETAFILE);
    public uint GetEnhMetaFileBits(IntPtr handle, uint size, byte[]? buffer) => Gdi.GetEnhMetaFileBits(handle, size, buffer);
    public IntPtr GetOpenClipboardWindow() => Gdi.GetOpenClipboardWindow();
    public uint GetWindowThreadProcessId(IntPtr window, out uint processId) => Gdi.GetWindowThreadProcessId(window, out processId);
}
