// init アクセサ・record のコンパイルに必要な型のシム。
// コンパイラは System.Runtime.CompilerServices.IsExternalInit の「存在」だけを要求し、
// BCL には .NET 5 以降にしか存在しない。netstandard2.0 / net48 ターゲットのプロジェクトへ
// src/Directory.Build.props がリンク挿入する（internal のためプロジェクト間で衝突しない）。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
