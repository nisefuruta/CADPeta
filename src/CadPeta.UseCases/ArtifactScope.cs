using System.Globalization;

namespace CadPeta.UseCases;

public sealed class ArtifactScope : IDisposable
{
    // CreateNew の衝突を表す Win32 エラーコード（IOException.HResult の下位16bit）。
    // 80=ERROR_FILE_EXISTS / 183=ERROR_ALREADY_EXISTS
    const int ErrorFileExists = 80;
    const int ErrorAlreadyExists = 183;
    // 同一秒・並行実行の連番退避の上限（暴走防止。実運用では 1〜2 で収束する）
    const int MaxCollisionAttempts = 10_000;
    const string TimestampFormat = "yyyyMMdd_HHmmss";

    readonly string? _lockPath;
    readonly string? _deleteRoot;
    readonly Action<string>? _warn;
    bool _preserve;
    bool _disposed;

    ArtifactScope(string dir, string baseName, string? lockPath, string? deleteRoot,
                  Action<string>? warn)
    {
        Dir = dir;
        BaseName = baseName;
        _lockPath = lockPath;
        _deleteRoot = deleteRoot;
        _warn = warn;
    }

    public string Dir { get; }
    public string BaseName { get; }

    // basename の原子的予約。予約ロックは利用者の出力フォルダではなく一時領域
    // （%TEMP%\CadPeta\locks）へ置く: 出力フォルダ（DWG と同じ場所）に .lock を作ると、
    // 異常終了（プロセス強制終了・電源断）で 0 バイトの残骸が利用者の作業フォルダに
    // 溜まるため。ロック名は「出力先フォルダ＋basename」から決まるプロセス間安定な
    // トークンにし、別フォルダの同名実行が偽競合しないようにする。
    public static ArtifactScope ReserveInDir(string dir, string stem, string layout, DateTime now,
                                             Action<string>? warn = null)
    {
        if (dir == null) throw new ArgumentNullException(nameof(dir));
        Directory.CreateDirectory(dir);
        string locksRoot = LocksRoot;
        Directory.CreateDirectory(locksRoot);
        string rootName = BuildBaseName(stem, layout, now);

        for (int attempt = 1; attempt <= MaxCollisionAttempts; attempt++)
        {
            string baseName = attempt == 1
                ? rootName
                : rootName + "_" + attempt.ToString(CultureInfo.InvariantCulture);
            // 予約対象は「その出力先に置く basename の成果物一式」。フォルダ＋basename から
            // 安定トークンを作り、同一対象を狙う並行実行だけが競合するようにする
            string token = StableToken(Path.GetFullPath(Path.Combine(dir, baseName)));
            string lockPath = Path.Combine(locksRoot, token + ".lock");
            try
            {
                using (new FileStream(lockPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                // 前回scopeが正常解放済みでも成果物は利用者フォルダに残る。同一秒の次回実行が
                // 同じbasenameを再取得して上書きしないよう、既存成果物も予約済みとして扱う。
                if (Directory.EnumerateFiles(dir, baseName + ".*").Any()
                    || File.Exists(Path.Combine(dir, baseName)))
                {
                    File.Delete(lockPath);
                    continue;
                }
                return new ArtifactScope(dir, baseName, lockPath, null, warn);
            }
            catch (IOException ex) when (IsAlreadyExists(ex))
            {
                // 別プロセスがこの basename を予約済み。衝突エラーだけ次の連番へ進める
            }
        }

        throw new IOException(MessageCatalogs.Current.Format(
            "artifact.reserve_failed", MaxCollisionAttempts, dir, rootName));
    }

    public static ArtifactScope CreateTemp(string stem, string layout, DateTime now,
                                           Action<string>? warn = null, string? tempRoot = null)
    {
        string root = Path.GetFullPath(tempRoot ?? DefaultTempRoot);
        string defaultRoot = Path.GetFullPath(DefaultTempRoot);
        if (!string.Equals(root.TrimEnd(Path.DirectorySeparatorChar),
                           defaultRoot.TrimEnd(Path.DirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase) &&
            !IsUnderRoot(root, defaultRoot))
            throw new IOException(MessageCatalogs.Current.Format("artifact.temp_root_outside", root));
        Directory.CreateDirectory(root);
        string dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new ArtifactScope(dir, BuildBaseName(stem, layout, now), null, root, warn);
    }

    public static string DefaultTempRoot => Path.Combine(Path.GetTempPath(), "CadPeta");

    // 予約ロックの置き場（利用者フォルダを汚さないため一時領域に集約する）
    static string LocksRoot => Path.Combine(DefaultTempRoot, "locks");

    // プロセス間で安定なトークン。String.GetHashCode は既定でプロセスごとに乱数化され
    // 別プロセスと一致しないため、予約ロック名には使えない。FNV-1a（64bit）で畳み込む。
    // Windows のパスは大文字小文字を区別しないため小文字化してから処理する
    internal static string StableToken(string s)
    {
        const ulong FnvOffset = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;
        ulong hash = FnvOffset;
        foreach (char c in s.ToLowerInvariant())
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    public void PreserveOnDispose() => _preserve = true;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_lockPath != null)
            TryCleanup(() => File.Delete(_lockPath),
                       MessageCatalogs.Current.Format("artifact.lock_delete_failed", _lockPath));

        if (_deleteRoot != null && !_preserve)
        {
            if (!IsUnderRoot(Dir, _deleteRoot))
            {
                Warn(MessageCatalogs.Current.Format("artifact.unsafe_delete_refused", Dir));
                return;
            }
            TryCleanup(() => Directory.Delete(Dir, recursive: true),
                       MessageCatalogs.Current.Format("artifact.temp_delete_failed", Dir));
        }
    }

    void TryCleanup(Action cleanup, string failureMessage)
    {
        try { cleanup(); }
        catch (Exception ex) when (IsCleanupFailure(ex))
        {
            Warn(MessageCatalogs.Current.Format("artifact.warn_frame", failureMessage, ex.Message));
        }
    }

    void Warn(string message)
    {
        try { _warn?.Invoke(message); }
        catch { /* 後始末の警告通知が元の結果（成功・例外）を置き換えてはならない */ }
    }

    internal static string BuildBaseName(string stem, string layout, DateTime now) =>
        Sanitize(stem) + "_" + Sanitize(layout) + "_" +
        now.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    internal static string Sanitize(string name)
    {
        if (name == null) throw new ArgumentNullException(nameof(name));
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    internal static bool IsAlreadyExists(IOException ex)
    {
        int code = ex.HResult & 0xffff;
        return code == ErrorFileExists || code == ErrorAlreadyExists;
    }

    internal static bool IsUnderRoot(string path, string root)
    {
        string fullPath = EnsureTrailingSeparator(Path.GetFullPath(path));
        string fullRoot = EnsureTrailingSeparator(Path.GetFullPath(root));
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    static string EnsureTrailingSeparator(string path) =>
        path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    static bool IsCleanupFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or
        NotSupportedException or ArgumentException;
}
