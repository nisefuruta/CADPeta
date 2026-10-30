using System.Text;

namespace CadPeta.UseCases;

/// <summary>出力ファイル(SVG・診断の JSON・ログ)の文字コード。UTF-8 の Encoding を作るのはこの 1 か所だけにする。</summary>
public static class TextEncodings
{
    /// <summary>
    /// BOM を付けない UTF-8。SVG と JSON は BOM を付けない UTF-8 が標準で、BOM があるとブラウザ以外の読み手が先頭の 3 バイトを誤読する。
    /// </summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
