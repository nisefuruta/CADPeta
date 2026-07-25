// PDF の BaseFont 名（PostScript 名）からファミリ名を解決する処理のテスト。
//
// なぜ要るか: PostScript 名はファミリ名と一致しない書体が多い。GDI は未知の名前でも失敗せず
// 既定書体へ黙って差し替えるため、生名のまま流すと字形も字送りも変わったまま出力される
// （実測: TimesNewRomanPSMT・CourierNewPSMT・YuGothic-Regular はいずれも Microsoft Sans Serif
// に化けた）。名前が同じなら同じ書体だという想定を置かず、解決結果を読み戻して検証する。
//
// 本テストは Windows に標準で入る書体だけを対象にする（GDI と実フォントに依存するため）。
using CadPeta.Acquire.Common;
using Xunit;

namespace CadPeta.Tests;

public class GdiFontMetricsTests
{
    [Theory]
    // PostScript 名の慣用接尾（MT）を落とす
    [InlineData("ArialMT", "Arial")]
    // 字体を表す接尾（-Bold・-Italic）を落として語幹へ戻す
    [InlineData("Arial-BoldMT", "Arial")]
    [InlineData("Arial-ItalicMT", "Arial")]
    // 連結表記を分かち書きへ戻す。頭字語は割らない
    [InlineData("MS-Gothic", "MS Gothic")]
    [InlineData("MS-PGothic", "MS PGothic")]
    [InlineData("MS-Mincho", "MS Mincho")]
    // PSMT 接尾＋連結表記。旧実装の固定辞書に無く、黙って別書体へ化けていた
    [InlineData("TimesNewRomanPSMT", "Times New Roman")]
    [InlineData("CourierNewPSMT", "Courier New")]
    // 名前がそのままファミリ名の書体
    [InlineData("Arial", "Arial")]
    public void PostScript名からファミリ名を解決する(string postScriptName, string expected)
    {
        Assert.Equal(expected, GdiFontMetrics.FamilyNameForPostScriptName(postScriptName));
    }

    [Fact]
    public void 解決できない名前は_黙って別書体に化けさせずnullを返す()
    {
        // 呼び出し側は null を受けて診断へ記録し、生名のまま出力する（未知入力を黙って捨てない）
        Assert.Null(GdiFontMetrics.FamilyNameForPostScriptName("CadPetaNoSuchFont-Regular"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void 空の名前は解決対象にしない(string name)
    {
        Assert.Null(GdiFontMetrics.FamilyNameForPostScriptName(name));
    }

    [Fact]
    public void 同じ名前を繰り返し解決しても同じ結果になる()
    {
        // 解決結果はキャッシュする。キャッシュの有無で結果が変わらないことを固定する
        string? first = GdiFontMetrics.FamilyNameForPostScriptName("TimesNewRomanPSMT");
        string? second = GdiFontMetrics.FamilyNameForPostScriptName("TimesNewRomanPSMT");
        Assert.Equal(first, second);
        Assert.Equal("Times New Roman", second);
    }
}
