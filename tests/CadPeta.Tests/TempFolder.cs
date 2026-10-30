using System;
using System.IO;

namespace CadPeta.Tests;

/// <summary>
/// 試験ごとに別の一時フォルダ(%TEMP%\CadPeta.Tests\&lt;Guid&gt;)を作り、Dispose で中身ごと削除する。
/// 試験が一時の場所を作るのはこの型だけにする(TestCodeRulesTests が検査する)。
/// </summary>
internal sealed class TempFolder : IDisposable
{
    /// <summary>フォルダの名前の既定の長さ(Guid の 32 桁)。</summary>
    private const int FullIdLength = 32;

    /// <summary>
    /// パスの長さで結果が変わる試験(install.bat の 260 文字の試験など)のための短い名前の長さ。
    /// パスの長さを試験が決められるように、試験のフォルダのパスを短くする。
    /// </summary>
    public const int ShortIdLength = 8;

    public TempFolder() : this(FullIdLength) { }

    public TempFolder(int idLength)
    {
        Path = System.IO.Path.Combine(Root, Guid.NewGuid().ToString("N").Substring(0, idLength));
        Directory.CreateDirectory(Path);
    }

    /// <summary>一時フォルダを作る親のフォルダ。</summary>
    public static string Root => System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CadPeta.Tests");

    /// <summary>作ったフォルダの絶対パス。</summary>
    public string Path { get; }

    /// <summary>フォルダの中のファイルのパス(ファイルは作らない)。</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        // 試験の後始末。削除に失敗しても(ほかのプロセスがファイルを開いているなど)試験の合否は変わらないので、例外を出さない。
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
