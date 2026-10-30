using System;
using System.IO;
using System.Globalization;

namespace CadPeta.UseCases;

public static class ArtifactRecovery
{
    const int MaxCollisionAttempts = 10_000;

    public static string DefaultRoot => Path.Combine(ArtifactScope.DefaultTempRoot, "Recovered");

    public static string? PreserveEmf(string emfPath, string? recoveryRoot = null,
                                      Action<string>? warn = null)
    {
        string root = Path.GetFullPath(recoveryRoot ?? DefaultRoot);
        try
        {
            // 代替として案内するのは、Officeが読み得る完結したEMFだけに限る。
            // 0-byteや描画途中のEMFをRecoveredへ移して「利用可能」と案内してはならない。
            ArtifactFile.Validate(emfPath, ArtifactKind.Emf);
            if (!ArtifactScope.IsUnderRoot(root, ArtifactScope.DefaultTempRoot))
                throw new IOException(MessageCatalogs.Current.Format("artifact.recovery_root_outside", root));
            Directory.CreateDirectory(root);

            string stem = Path.GetFileNameWithoutExtension(emfPath);
            for (int attempt = 1; attempt <= MaxCollisionAttempts; attempt++)
            {
                string suffix = attempt == 1 ? "" : "_" + attempt.ToString(CultureInfo.InvariantCulture);
                string destination = Path.Combine(root, stem + suffix + ".emf");
                string reservation = destination + ".lock";
                try
                {
                    using (new FileStream(reservation, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                }
                catch (IOException ex) when (ArtifactScope.IsAlreadyExists(ex))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(destination))
                        continue;
                    try
                    {
                        File.Move(emfPath, destination);
                        return destination;
                    }
                    catch (IOException ex) when (ArtifactScope.IsAlreadyExists(ex))
                    {
                        continue;
                    }
                }
                finally
                {
                    try { File.Delete(reservation); }
                    catch (Exception ex) when (IsIoFailure(ex))
                    {
                        SafeWarn(warn, MessageCatalogs.Current.Format(
                            "artifact.recovery_lock_delete_failed", reservation, ex.Message));
                    }
                }
            }
            throw new IOException(MessageCatalogs.Current.Format(
                "artifact.recovery_reserve_failed", MaxCollisionAttempts, root));
        }
        catch (Exception ex) when (IsIoFailure(ex))
        {
            SafeWarn(warn, MessageCatalogs.Current.Format(
                "artifact.recovery_preserve_failed", emfPath, ex.Message));
            return null;
        }
    }

    static void SafeWarn(Action<string>? warn, string message)
    {
        try { warn?.Invoke(message); }
        catch { }
    }

    static bool IsIoFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.SecurityException or
        NotSupportedException or ArgumentException;
}
