using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace CadPeta.Tests;

/// <summary>
/// フォルダの中のファイルとフォルダの一覧を、前後の比較のための文字列の並びにする。比べるのは名前とファイルの内容の SHA256 で、
/// 更新時刻とサイズは比べない(tests/tools/common/test_helpers.ps1 の Get-FolderListing と同じ規則)。
/// 読めないファイルは例外にする(読めないまま一致と判定しないため)。
/// </summary>
internal static class FolderSnapshot
{
    /// <summary>相対パス(フォルダは末尾に \)と SHA256 の行を、序数の順に並べたもの。</summary>
    public static string[] Of(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Select(path => Directory.Exists(path)
            ? Path.GetRelativePath(root, path) + "\\"
            : Path.GetRelativePath(root, path) + "=" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
        .OrderBy(line => line, StringComparer.Ordinal).ToArray();
}
