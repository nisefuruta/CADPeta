using System;
using CadPeta.UseCases;
using Xunit;

namespace CadPeta.Tests;

// PETA の成果物のファイル名の形（PETAOPT の設定）。差し込みは {drawing}・{tab}・{datetime}・{seq}・{comment} の 5 つで、
// 名前は言語で変えない。知らない差し込みとファイル名に使えない文字は設定するときに弾く
public class FileNameTemplateTests
{
    static readonly DateTime Now = new(2026, 10, 28, 17, 5, 9);

    static FileNameFields Fields(string comment = "") => new("001_平面図", "PETA_A3", Now, comment);

    [Fact]
    public void 既定の形は今までと同じ図面名_レイアウト名_日時()
    {
        Assert.Equal("001_平面図_PETA_A3_20261028_170509", FileNameTemplate.DefaultName("001_平面図", "PETA_A3", Now));
    }

    [Theory]
    [InlineData("{drawing}_{comment}", "打合せ用", "001_平面図_打合せ用")]
    [InlineData("{drawing}_{comment}", "", "001_平面図")]
    [InlineData("{comment}_{drawing}", "", "001_平面図")]
    [InlineData("{drawing}_{comment}_{tab}", "", "001_平面図_PETA_A3")]
    public void コメントが空なら差し込みと前後の区切りを詰める(string template, string comment, string expected)
    {
        Assert.Equal(expected, FileNameTemplate.Parse(template).Template!.Expand(Fields(comment), seq: 1));
    }

    // 区切りを詰めるのは空の差し込みの隣だけ。図面名や形に書いた連続した区切りは変えない（違う名前が同じ名前に縮まない）
    [Theory]
    [InlineData("{drawing}_{tab}", "A__B", "A__B_PETA_A3")]
    [InlineData("__{drawing}", "A", "__A")]
    [InlineData("{drawing}__{comment}", "A", "A_")]
    public void 区切りを詰めるのは空の差し込みの隣だけ(string template, string drawing, string expected)
    {
        var fields = new FileNameFields(drawing, "PETA_A3", Now, "");

        Assert.Equal(expected, FileNameTemplate.Parse(template).Template!.Expand(fields, seq: 1));
    }

    [Fact]
    public void 図面名とレイアウト名のファイル名に使えない文字は置き換える()
    {
        var fields = new FileNameFields("a:b", "c/d", Now, "");

        Assert.Equal("a_b_c_d", FileNameTemplate.Parse("{drawing}_{tab}").Template!.Expand(fields, seq: 1));
    }

    [Fact]
    public void 連番は3桁()
    {
        Assert.Equal("001_平面図_007", FileNameTemplate.Parse("{drawing}_{seq}").Template!.Expand(Fields(), seq: 7));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{drawing}_{unknown}")]
    [InlineData("{drawing")]
    [InlineData("a/b_{drawing}")]
    [InlineData("a:b_{drawing}")]
    [InlineData("{comment}")]
    [InlineData("CON")]
    [InlineData("nul")]
    [InlineData("COM1")]
    [InlineData("LPT9")]
    [InlineData("{comment}CON")]
    [InlineData("{drawing}.")]
    [InlineData("{drawing} ")]
    [InlineData("{drawing}_{seq}_{seq}")]
    public void 使えない形は理由を返して受け付けない(string template)
    {
        var parsed = FileNameTemplate.Parse(template);

        Assert.Null(parsed.Template);
        Assert.False(string.IsNullOrEmpty(parsed.Error));
    }

    [Theory]
    [InlineData("{drawing}_{comment}", true)]
    [InlineData("{drawing}_{tab}_{datetime}", false)]
    public void コメントを聞くのは形にcommentがあるときだけ(string template, bool expected)
    {
        Assert.Equal(expected, FileNameTemplate.Parse(template).Template!.UsesComment);
    }

    // コメントは差し込んだ後のファイル名で確かめる（使えない文字のほか、Windows の予約の名前と末尾の . と空白）
    [Theory]
    [InlineData("{drawing}_{comment}", "打合せ用", true)]
    [InlineData("{drawing}_{comment}", "", true)]
    [InlineData("{drawing}_{comment}", "a/b", false)]
    [InlineData("{drawing}_{comment}", "a?b", false)]
    [InlineData("{drawing}_{comment}", "abc.", false)]
    [InlineData("{comment}.{drawing}", "con", false)]
    [InlineData("{comment}.{drawing}", "con1", true)]
    public void コメントは差し込んだ名前がファイル名に使えるときだけ受け付ける(string template, string comment, bool ok)
    {
        Assert.Equal(ok, FileNameTemplate.Parse(template).Template!.ValidateComment(Fields(comment)) == null);
    }

    [Fact]
    public void 次の連番は同じ形の名前の最大の番号に1を足す()
    {
        var template = FileNameTemplate.Parse("{drawing}_{seq}").Template!;
        var existing = new[] { "001_平面図_001.emf", "001_平面図_004.pdf", "001_平面図_004.emf", "001_平面図_x.emf", "別の図面_009.emf" };

        Assert.Equal(5, template.NextSeq(Fields(), existing));
    }

    // 既存のファイル名は CADPeta が作ったとは限らない。番号として読めない名前（桁があふれる、全角数字）は無視する
    [Fact]
    public void 番号として読めない名前は連番の計算から外す()
    {
        var template = FileNameTemplate.Parse("{drawing}_{seq}").Template!;
        var existing = new[] { "001_平面図_2147483648.emf", "001_平面図_００９.emf", "001_平面図_003.emf" };

        Assert.Equal(4, template.NextSeq(Fields(), existing));
    }

    [Fact]
    public void 同じ形の名前が無ければ連番は1から()
    {
        var template = FileNameTemplate.Parse("{drawing}_{seq}").Template!;

        Assert.Equal(1, template.NextSeq(Fields(), Array.Empty<string>()));
    }
}
