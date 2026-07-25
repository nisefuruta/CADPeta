using CadPeta.Gui;
using Xunit;

namespace CadPeta.Gui.Tests;

public sealed class GuiLifecycleTests
{
    [Fact]
    public void Repeated_close_is_cancelled_until_shutdown_is_ready()
    {
        Assert.True(ShutdownClosePolicy.ShouldCancel(closing: true, ready: false));
        Assert.False(ShutdownClosePolicy.ShouldCancel(closing: true, ready: true));
        Assert.False(ShutdownClosePolicy.ShouldCancel(closing: false, ready: false));
    }

    [Fact]
    public void Dispose_removes_session_without_clipboard_emf()
    {
        var session = new GuiSession();
        string directory = session.DirectoryPath;
        session.Dispose();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Dispose_keeps_clipboard_emf_and_cleanup_lock()
    {
        var session = new GuiSession();
        string directory = session.DirectoryPath;
        WriteValidEmf(session.NewPath(".emf"));
        session.Dispose();
        Assert.True(Directory.Exists(directory));
        Assert.True(File.Exists(Path.Combine(directory, "session.lock")));
        Directory.Delete(directory, true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Dispose_removes_session_with_empty_or_broken_emf(int kind)
    {
        var session = new GuiSession();
        string directory = session.DirectoryPath;
        File.WriteAllBytes(session.NewPath(".emf"), kind == 0 ? Array.Empty<byte>() : new byte[] { 1 });
        session.Dispose();
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void Startup_removes_unlocked_empty_crash_residue_immediately()
    {
        string root = Path.Combine(Path.GetTempPath(), "cadpeta", "gui");
        Directory.CreateDirectory(root);
        string residue = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(residue);
        File.WriteAllBytes(Path.Combine(residue, "session.lock"), Array.Empty<byte>());
        using var session = new GuiSession();
        Assert.False(Directory.Exists(residue));
    }

    static void WriteValidEmf(string path) =>
        File.WriteAllBytes(path, CadPeta.Tests.MinimalArtifactBytes.Emf());
}
