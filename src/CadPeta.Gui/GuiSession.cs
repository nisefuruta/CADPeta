namespace CadPeta.Gui;

using CadPeta.UseCases;

internal sealed class GuiSession : IDisposable
{
    public const int ClipboardEmfRetentionDays = 7;
    readonly FileStream _lock;
    public string DirectoryPath { get; }

    public GuiSession()
    {
        string root = Path.Combine(Path.GetTempPath(), "cadpeta", "gui");
        Directory.CreateDirectory(root);
        Cleanup(root);
        DirectoryPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        _lock = new FileStream(Path.Combine(DirectoryPath, "session.lock"), FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
    }

    public string NewPath(string extension, string? baseName = null) => Path.Combine(DirectoryPath,
        $"{SafeName(baseName ?? "cadpeta")}-{Guid.NewGuid():N}{extension}");

    public void Dispose()
    {
        _lock.Dispose();
        try
        {
            // Clipboard代替用EMFがあるセッションだけ保持する。プレビューだけの正常終了では
            // ディレクトリを即時削除し、起動のたびに空ディレクトリを残さない。
            if (!HasValidEmf(DirectoryPath))
                Directory.Delete(DirectoryPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    static string SafeName(string value)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
        return string.IsNullOrWhiteSpace(value) ? "cadpeta" : value;
    }

    static void Cleanup(string root)
    {
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(dir), "N", out _)) continue;
            try
            {
                using var probe = new FileStream(Path.Combine(dir, "session.lock"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None);
                bool hasEmf = HasValidEmf(dir);
                bool expired = Directory.GetLastWriteTimeUtc(dir) <= DateTime.UtcNow.AddDays(-ClipboardEmfRetentionDays);
                // 強制終了で残ったプレビュー/空セッションは次回起動時に即時清掃する。
                // 利用者の代替挿入用EMFだけは保持期間を満了するまで残す。
                if (!hasEmf || expired) { probe.Dispose(); Directory.Delete(dir, true); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    static bool HasValidEmf(string dir)
    {
        bool found = false;
        foreach (string emf in Directory.EnumerateFiles(dir, "*.emf"))
        {
            try { ArtifactFile.Validate(emf, ArtifactKind.Emf); found = true; }
            catch (InvalidDataException)
            {
                try { File.Delete(emf); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return found;
    }
}
