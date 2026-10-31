using System;
using System.IO;

namespace CadPeta.UseCases;

/// <summary>ファイルとフォルダの操作の失敗の扱い（どの例外を「その場所は使えない」とみなすか、利用者に見せる理由の形）。</summary>
public static class FileFailures
{
    /// <summary>
    /// パスの操作・ファイルの作成と書き込みで「その場所は使えない」を意味する例外か。これ以外（メモリ不足などの実行の異常）は
    /// 捕まえずに呼び出し元へ通す。
    /// </summary>
    public static bool IsPathFailure(Exception ex) =>
        ex is IOException                         // PathTooLongException・DirectoryNotFoundException を含む
           or UnauthorizedAccessException         // アクセス許可が無い
           or System.Security.SecurityException
           or NotSupportedException               // "con:" などの使えない形
           or ArgumentException;                  // 使えない文字・空のパス

    /// <summary>例外の文を、利用者に見せる 1 行にする（.NET Framework の IO の例外の文は末尾に改行が付く）。</summary>
    public static string OneLine(string message) => message.Replace("\r", " ").Replace("\n", " ").Trim();
}