using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public sealed class ArtifactRecoveryTests
{
    [Fact]
    public void PreserveEmf_ThenDispose_RemovesPdfAndWorkDirectory()
    {
        string recovered = Path.Combine(ArtifactScope.DefaultTempRoot, "RecoveredTests", Guid.NewGuid().ToString("N"));
        string? work = null;
        string? preserved = null;
        try
        {
            using (var scope = ArtifactScope.CreateTemp("drawing", "Model", DateTime.Now))
            {
                work = scope.Dir;
                string emf = Path.Combine(work, "drawing.emf");
                WriteValidEmf(emf, 1);
                File.WriteAllText(Path.Combine(work, "drawing.pdf"), "pdf");

                preserved = ArtifactRecovery.PreserveEmf(emf, recovered);
                Assert.NotNull(preserved);
            }

            Assert.False(Directory.Exists(work));
            Assert.True(File.Exists(preserved));
            Assert.Equal(new[] { Path.GetFileName(preserved) },
                         Directory.GetFiles(recovered).Select(Path.GetFileName));
        }
        finally
        {
            if (work != null && Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(recovered)) Directory.Delete(recovered, true);
        }
    }

    [Fact]
    public void PreserveEmf_MovesOnlyEmfAndDoesNotOverwrite()
    {
        string root = ArtifactScope.DefaultTempRoot;
        string work = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string recovered = Path.Combine(root, "RecoveredTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string first = Path.Combine(work, "drawing.emf");
            WriteValidEmf(first, 1);
            string? firstOut = ArtifactRecovery.PreserveEmf(first, recovered);
            Assert.NotNull(firstOut);

            string second = Path.Combine(work, "drawing.emf");
            WriteValidEmf(second, 2);
            string? secondOut = ArtifactRecovery.PreserveEmf(second, recovered);
            Assert.NotNull(secondOut);
            Assert.NotEqual(firstOut, secondOut);
            Assert.Equal(1u, ReadU32(File.ReadAllBytes(firstOut!), 56));
            Assert.Equal(2u, ReadU32(File.ReadAllBytes(secondOut!), 56));
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(recovered)) Directory.Delete(recovered, true);
        }
    }

    [Fact]
    public void PreserveEmf_RejectsRootOutsideCadPetaTemp()
    {
        string work = Path.Combine(ArtifactScope.DefaultTempRoot, Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(), "OutsideCadPeta", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var warnings = new List<string>();
        try
        {
            string emf = Path.Combine(work, "x.emf");
            WriteValidEmf(emf, 1);
            Assert.Null(ArtifactRecovery.PreserveEmf(emf, outside, warnings.Add));
            Assert.True(File.Exists(emf));
            Assert.Single(warnings);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(outside)) Directory.Delete(outside, true);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PreserveEmf_RejectsEmptyOrBrokenEmf(int kind)
    {
        string work = Path.Combine(ArtifactScope.DefaultTempRoot, Guid.NewGuid().ToString("N"));
        string recovered = Path.Combine(ArtifactScope.DefaultTempRoot, "RecoveredTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var warnings = new List<string>();
        try
        {
            string emf = Path.Combine(work, "broken.emf");
            File.WriteAllBytes(emf, kind == 0 ? Array.Empty<byte>() : new byte[] { 1, 2, 3 });
            Assert.Null(ArtifactRecovery.PreserveEmf(emf, recovered, warnings.Add));
            Assert.True(File.Exists(emf));
            Assert.False(Directory.Exists(recovered));
            Assert.Single(warnings);
        }
        finally
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            if (Directory.Exists(recovered)) Directory.Delete(recovered, true);
        }
    }

    static void WriteValidEmf(string path, uint marker) =>
        File.WriteAllBytes(path, MinimalArtifactBytes.Emf(marker));

    static uint ReadU32(byte[] b, int o) => MinimalArtifactBytes.ReadU32(b, o);
}
