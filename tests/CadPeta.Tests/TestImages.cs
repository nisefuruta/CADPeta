using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace CadPeta.Tests;

/// <summary>試験が入力の画像を作るときの補助。</summary>
internal static class TestImages
{
    /// <summary>ビットマップを GDI+ で PNG のバイト列にする。</summary>
    public static byte[] EncodePng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}
