// PDF埋め込み画像のデコードと SMask 合成（フィルタ伸長は PdfPig、JPEG のみ System.Drawing）。
// SOLIDハッチ×透過の JPEG+SMask 画像化（PDF印刷経路の非可逆類型）を RGBA 合成 PNG として IR へ渡す。
// OLE貼付（Excel表など）のプロット結果である CCITTFaxDecode の 1bit Indexed 画像もここで画素化する。
using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Filters;
using UglyToad.PdfPig.Tokens;

namespace CadPeta.Acquire.Pdf;

internal static class PdfImageDecoder
{
    /// <param name="quarterTurnsCcw">埋め込み前に画素へ焼き込む反時計回りの四半回転数（0〜3）。
    /// /Rotate ページで PdfPig が返す回転矩形（PdfIrAcquirer 参照）を軸平行配置へ直すために使う。
    /// 四半回転の画素回転は無劣化。</param>
    public static byte[] ComposePng(UglyToad.PdfPig.Content.IPdfImage im, PdfDocument doc,
                                    out bool hadSMask, int quarterTurnsCcw = 0)
    {
        var dict = im.ImageDictionary;
        byte[] color = ApplyFilters(im.RawBytes.ToArray(), dict, out bool colorIsJpeg);
        using var colorBmp = DecodeColorBitmap(color, colorIsJpeg, dict,
                                               im.WidthInSamples, im.HeightInSamples);

        hadSMask = false;
        byte[]? alpha = null;
        int aw = 0, ah = 0;
        // SMask は間接参照のことも解決済み StreamToken のこともある。両対応で読む
        StreamToken? sm = null;
        if (dict.TryGet(NameToken.Create("SMask"), out var smTok))
            sm = smTok as StreamToken
                 ?? (smTok is IndirectReferenceToken smRef
                     ? doc.Structure.GetObject(smRef.Data).Data as StreamToken : null);
        if (sm != null)
        {
            hadSMask = true;
            alpha = ApplyFilters(sm.Data.ToArray(), sm.StreamDictionary, out bool aJpeg);
            if (aJpeg) throw new NotSupportedException("SMask が DCT（未対応）");
            aw = IntOf(sm.StreamDictionary, "Width");
            ah = IntOf(sm.StreamDictionary, "Height");
        }

        // 1bit画像（OLE貼付のCCITT等）は SMask を伴わない限り 1bpp パレットのまま PNG 化する。
        // 32bpp へ展開すると白黒表の PNG が桁違いに肥大するため、展開合成は透過合成が
        // 必要なときに限る
        if (colorBmp.PixelFormat == PixelFormat.Format1bppIndexed && alpha == null)
        {
            BakeQuarterRotation(colorBmp, quarterTurnsCcw);
            return SaveAsPng(colorBmp);
        }

        int w = colorBmp.Width, h = colorBmp.Height;
        using var outBmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(outBmp))
            g.DrawImage(colorBmp, 0, 0, w, h);

        if (alpha != null)
        {
            var data = outBmp.LockBits(new Rectangle(0, 0, w, h),
                                       ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            unsafe
            {
                for (int y = 0; y < h; y++)
                {
                    byte* row = (byte*)data.Scan0 + y * data.Stride;
                    int sy = ah == h ? y : y * ah / h;
                    for (int x = 0; x < w; x++)
                    {
                        int sx = aw == w ? x : x * aw / w;
                        row[x * 4 + 3] = alpha[sy * aw + sx];
                    }
                }
            }
            outBmp.UnlockBits(data);
        }

        BakeQuarterRotation(outBmp, quarterTurnsCcw);
        return SaveAsPng(outBmp);
    }

    // RotateFlipType は時計回り定義のため、反時計回り指定を読み替える
    static void BakeQuarterRotation(Bitmap bmp, int quarterTurnsCcw)
    {
        switch (((quarterTurnsCcw % 4) + 4) % 4)
        {
            case 1: bmp.RotateFlip(RotateFlipType.Rotate270FlipNone); break;
            case 2: bmp.RotateFlip(RotateFlipType.Rotate180FlipNone); break;
            case 3: bmp.RotateFlip(RotateFlipType.Rotate90FlipNone); break;
        }
    }

    static byte[] SaveAsPng(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    static int IntOf(DictionaryToken d, string key)
        => d.TryGet(NameToken.Create(key), out var v) && v is NumericToken n ? n.Int : 0;

    // 非JPEGの色ストリームを ColorSpace / BitsPerComponent に従って復元する。
    // DeviceRGB は3バイト/画素・DeviceGray は1バイト/画素。両者を取り違えると画素が総崩れ
    // するため、色空間・ビット深度は画像辞書から読み、想定外は握りつぶさず例外で止める。
    static Bitmap DecodeColorBitmap(byte[] color, bool isJpeg, DictionaryToken dict, int w, int h)
    {
        if (isJpeg) return new Bitmap(new MemoryStream(color));

        int bits = IntOf(dict, "BitsPerComponent");
        if (bits == 1) return OneBitToBitmap(color, w, h, ResolveBiLevelPalette(dict, w, h));
        if (bits != 8)
            throw new NotSupportedException(
                $"未対応の画像ビット深度: BitsPerComponent={bits}（1・8のみ対応）。" +
                $"画像サイズ {w}x{h}px");

        // ColorSpace は Name（DeviceRGB / DeviceGray）で持つ。配列（Indexed・ICCBased 等）は
        // 実プロットPDF（DCT系＝isJpeg 経路）・自作PDF（DeviceRGB）のいずれにも現れない
        string cs = dict.TryGet(NameToken.Create("ColorSpace"), out var csTok) && csTok is NameToken n
            ? n.Data : "";
        switch (cs)
        {
            case "DeviceRGB": return RgbToBitmap(color, w, h);
            case "DeviceGray": return GrayToBitmap(color, w, h);
            default:
                throw new NotSupportedException(
                    $"未対応の画像色空間: ColorSpace={(cs.Length == 0 ? "(不明)" : cs)}" +
                    $"（DeviceRGB / DeviceGray のみ対応）。画像サイズ {w}x{h}px");
        }
    }

    // 1bit画像の2値（サンプル値 0/1）が表す実色を ColorSpace から解く。
    // DeviceGray は 0=黒・1=白。Indexed はサンプル値がパレット添字なので、パレット
    // （16進または文字列リテラル）から基底色空間の成分数ぶんを読む。OLE貼付（Excel表など）の
    // プロット結果は [/Indexed /DeviceRGB 1 <000000 FFFFFF>] の形で現れる。
    // CCITTFax の復号値は BlackIs1 省略時の規定どおり 0=黒で、この対応と一致する
    static (Color c0, Color c1) ResolveBiLevelPalette(DictionaryToken dict, int w, int h)
    {
        if (dict.TryGet(NameToken.Create("ColorSpace"), out var csTok))
        {
            if (csTok is NameToken n && n.Data == "DeviceGray")
                return (Color.Black, Color.White);
            if (csTok is ArrayToken arr && arr.Data.Count == 4
                && arr.Data[0] is NameToken kind && kind.Data == "Indexed"
                && arr.Data[1] is NameToken baseCs)
            {
                byte[] lut = arr.Data[3] switch
                {
                    HexToken ht => ht.Bytes.ToArray(),
                    StringToken st => st.Data.Select(c => (byte)c).ToArray(),
                    var other => throw new NotSupportedException(
                        $"未対応のIndexedパレット表現: {other.GetType().Name}。画像サイズ {w}x{h}px"),
                };
                int comps = baseCs.Data switch
                {
                    "DeviceRGB" => 3,
                    "DeviceGray" => 1,
                    _ => throw new NotSupportedException(
                        $"未対応のIndexed基底色空間: {baseCs.Data}。画像サイズ {w}x{h}px"),
                };
                return (PaletteColor(lut, 0, comps), PaletteColor(lut, 1, comps));
            }
        }
        throw new NotSupportedException(
            $"未対応の1bit画像色空間: {(csTok == null ? "(不明)" : csTok.ToString())}" +
            $"（DeviceGray / Indexed のみ対応）。画像サイズ {w}x{h}px");
    }

    static Color PaletteColor(byte[] lut, int index, int comps)
    {
        int o = index * comps;
        if (lut.Length < o + comps)
            throw new FormatException($"Indexedパレットのバイト数が不足: {lut.Length} < {o + comps}");
        return comps == 3 ? Color.FromArgb(lut[o], lut[o + 1], lut[o + 2])
                          : Color.FromArgb(lut[o], lut[o], lut[o]);
    }

    // 1bit画像（MSB先頭・行はバイト境界へ詰める）を 1bpp インデックス Bitmap のまま組み立てる。
    // PDF の行詰め（(w+7)/8 バイト）と GDI の Stride（4バイト境界）の差だけ行単位で写す
    static Bitmap OneBitToBitmap(byte[] packed, int w, int h, (Color c0, Color c1) pal)
    {
        int stride1 = (w + 7) / 8;
        long need = (long)stride1 * h;
        if (packed.Length < need)
            throw new FormatException(
                $"1bit画像のバイト数が不足: {packed.Length} < {need}（{w}x{h}、行{stride1}バイト）");
        var bmp = new Bitmap(w, h, PixelFormat.Format1bppIndexed);
        var palette = bmp.Palette;
        palette.Entries[0] = pal.c0;
        palette.Entries[1] = pal.c1;
        bmp.Palette = palette;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                ImageLockMode.WriteOnly, PixelFormat.Format1bppIndexed);
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int i = 0; i < stride1; i++)
                    row[i] = packed[y * stride1 + i];
            }
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    static Bitmap RgbToBitmap(byte[] rgb, int w, int h)
    {
        long need = (long)w * h * 3;
        if (rgb.Length < need)
            throw new FormatException(
                $"DeviceRGB画像のバイト数が不足: {rgb.Length} < {need}（{w}x{h}×3バイト）");
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    int si = (y * w + x) * 3;
                    // PDF DeviceRGB は R,G,B 順、GDI 32bpp は B,G,R,A 順
                    row[x * 4] = rgb[si + 2]; row[x * 4 + 1] = rgb[si + 1];
                    row[x * 4 + 2] = rgb[si]; row[x * 4 + 3] = 255;
                }
            }
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    static Bitmap GrayToBitmap(byte[] gray, int w, int h)
    {
        long need = (long)w * h;
        if (gray.Length < need)
            throw new FormatException(
                $"DeviceGray画像のバイト数が不足: {gray.Length} < {need}（{w}x{h}）");
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        unsafe
        {
            for (int y = 0; y < h; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < w; x++)
                {
                    byte v = gray[y * w + x];
                    row[x * 4] = v; row[x * 4 + 1] = v; row[x * 4 + 2] = v; row[x * 4 + 3] = 255;
                }
            }
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    // /Filter 列の伸長は PdfPig のフィルタ実装へ委ねる（Flate の Predictor、CCITTFax の
    // DecodeParms 解釈などを自前実装で重複させない。未対応フィルタ名は PdfPig が例外で
    // 顕在化させる）。DCTDecode だけは PdfPig が復号を持たないため終端で JPEG のまま返し、
    // System.Drawing に委ねる
    static byte[] ApplyFilters(byte[] data, DictionaryToken dict, out bool isJpeg)
    {
        isJpeg = false;
        var filters = DefaultFilterProvider.Instance.GetFilters(dict);
        for (int i = 0; i < filters.Count; i++)
        {
            if (filters[i] is DctDecodeFilter)
            {
                isJpeg = true;
                return data;  // JPEG は System.Drawing に委ねる
            }
            data = filters[i].Decode(data, dict, DefaultFilterProvider.Instance, i).ToArray();
        }
        return data;
    }
}
