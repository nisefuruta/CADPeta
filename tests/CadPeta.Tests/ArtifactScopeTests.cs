using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

public sealed class ArtifactScopeTests
{
    const string Name = "d_l_20260712_143502";

    [Fact]
    public void ReserveInDir_UsesSuffixAndReleasesLocks()
    {
        string dir = NewTestDir();
        try
        {
            using var first = ArtifactScope.ReserveInDir(dir, Name);
            using var second = ArtifactScope.ReserveInDir(dir, Name);
            Assert.Equal("d_l_20260712_143502", first.BaseName);
            Assert.Equal("d_l_20260712_143502(1)", second.BaseName);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task ReserveInDir_IsUniqueInParallel()
    {
        string dir = NewTestDir();
        try
        {
            ArtifactScope[] scopes = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ =>
                Task.Run(() => ArtifactScope.ReserveInDir(dir, Name))));
            try { Assert.Equal(scopes.Length, scopes.Select(x => x.BaseName).Distinct().Count()); }
            finally { foreach (var scope in scopes) scope.Dispose(); }
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReserveInDir_DoesNotLitterOutputDirWithLock()
    {
        string dir = NewTestDir();
        try
        {
            using (var scope = ArtifactScope.ReserveInDir(dir, Name))
            {
                // 予約ロックは一時領域に置く。利用者の出力フォルダには .lock を残さない
                Assert.Empty(Directory.GetFiles(dir, "*.lock"));
                Assert.Equal("d_l_20260712_143502", scope.BaseName);
            }
            Assert.Empty(Directory.GetFiles(dir, "*.lock"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReserveInDir_PreviousScopeReleasedButArtifactExists_UsesSuffix()
    {
        string dir = NewTestDir();
        try
        {
            string firstName;
            using (var first = ArtifactScope.ReserveInDir(dir, Name))
            {
                firstName = first.BaseName;
                File.WriteAllText(Path.Combine(dir, first.BaseName + ".svg"), "既存成果物");
            }
            using var second = ArtifactScope.ReserveInDir(dir, Name);
            Assert.Equal(firstName + "(1)", second.BaseName);
            Assert.Equal("既存成果物", File.ReadAllText(Path.Combine(dir, firstName + ".svg")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(unchecked((int)0x80070050), true)]
    [InlineData(unchecked((int)0x800700B7), true)]
    [InlineData(unchecked((int)0x80070070), false)]
    public void IsAlreadyExists_RecognizesOnlyCollisionCodes(int hresult, bool expected)
    {
        Assert.Equal(expected, ArtifactScope.IsAlreadyExists(new IOException("test", hresult)));
    }

    [Fact]
    public void CreateTemp_DisposeDeletesDirectory()
    {
        string dir;
        using (var scope = ArtifactScope.CreateTemp(Name))
        {
            dir = scope.Dir;
            File.WriteAllText(Path.Combine(dir, "x.emf"), "x");
        }
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public void PreserveOnDispose_LeavesDirectory()
    {
        string dir;
        using (var scope = ArtifactScope.CreateTemp(Name))
        {
            dir = scope.Dir;
            scope.PreserveOnDispose();
        }
        try { Assert.True(Directory.Exists(dir)); }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void Dispose_DeleteFailureWarnsWithoutThrowing()
    {
        string? dir = null;
        var warnings = new List<string>();
        try
        {
            var scope = ArtifactScope.CreateTemp(Name, warnings.Add);
            dir = scope.Dir;
            string path = Path.Combine(dir, "locked.emf");
            File.WriteAllText(path, "x");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                scope.Dispose();
            Assert.Single(warnings);
            Assert.True(Directory.Exists(dir));
        }
        finally
        {
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Dispose_WarningCallbackFailureDoesNotEscape()
    {
        string? dir = null;
        try
        {
            var scope = ArtifactScope.CreateTemp(Name, _ => throw new Exception("コールバック失敗"));
            dir = scope.Dir;
            string path = Path.Combine(dir, "locked.emf");
            File.WriteAllText(path, "x");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
                scope.Dispose();
        }
        finally
        {
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void IsUnderRoot_RejectsSiblingPrefixAndRootItself()
    {
        using var temp = new TempFolder();
        string root = temp.File("CadPetaRoot");
        Assert.True(ArtifactScope.IsUnderRoot(Path.Combine(root, "child"), root));
        Assert.False(ArtifactScope.IsUnderRoot(root + "Else", root));
        Assert.False(ArtifactScope.IsUnderRoot(root, root));
    }

    static string NewTestDir()
    {
        string dir = Path.Combine(ArtifactScope.DefaultTempRoot, "Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
