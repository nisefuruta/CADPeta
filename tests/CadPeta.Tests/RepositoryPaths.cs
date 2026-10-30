using System;
using System.IO;
using System.Linq;

namespace CadPeta.Tests;

/// <summary>
/// 試験が入力に使うリポジトリ内のファイルの場所。ビルドの出力先から親フォルダをたどって CadPeta.sln を探す
/// (CadPeta.App.Tests へはリンク共有)。
/// </summary>
internal static class RepositoryPaths
{
    /// <summary>CadPeta.sln があるフォルダ。見つからなければ例外。</summary>
    public static string Root()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "CadPeta.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("CadPeta.sln を親ディレクトリから発見できない");
    }

    /// <summary>tests/assets 配下のファイルの絶対パス。</summary>
    public static string Asset(params string[] relativeParts) =>
        Path.Combine(new[] { Root(), "tests", "assets" }.Concat(relativeParts).ToArray());

    /// <summary>tests/assets/pdf-fixtures 配下の PDF の絶対パス。</summary>
    public static string PdfFixture(string name) => Asset("pdf-fixtures", name);
}
