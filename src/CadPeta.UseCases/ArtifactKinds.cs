using System;
using System.Collections.Generic;
using System.Linq;

namespace CadPeta.UseCases;

/// <summary>
/// 成果物の形式（<see cref="ArtifactKind"/>）の名前と拡張子。設定のファイルに書く名前と拡張子は、どちらも列挙の名前を小文字にしたもので、
/// ほかの場所に形式ごとの対応表を書かない。
/// </summary>
public static class ArtifactKinds
{
    /// <summary>すべての形式（列挙の順）。</summary>
    public static IReadOnlyList<ArtifactKind> All { get; } = Enum.GetValues(typeof(ArtifactKind)).Cast<ArtifactKind>().ToArray();

    /// <summary>設定のファイルに書く名前（言語で変えない）。</summary>
    public static string Name(ArtifactKind kind) => kind.ToString().ToLowerInvariant();

    /// <summary>画面とコマンドラインに出す名前（PDF・SVG・EMF・PNG）。</summary>
    public static string Label(ArtifactKind kind) => Name(kind).ToUpperInvariant();

    /// <summary>ファイルの拡張子（. を含む）。</summary>
    public static string Extension(ArtifactKind kind) => "." + Name(kind);

    /// <summary>名前から形式を返す。知らない名前（大文字と小文字の違いを含む）は null。</summary>
    public static ArtifactKind? FromName(string name)
    {
        foreach (var kind in All)
            if (string.Equals(Name(kind), name, StringComparison.Ordinal))
                return kind;
        return null;
    }

    /// <summary>形式を 1 つも出さないことを表す一覧の入力（-OSNAP の NONE と同じ）。</summary>
    public const string NoneKeyword = "NONE";

    /// <summary>
    /// 形式の一覧の入力（-PETAOPT。-OSNAP と同じく、オンにする形式の名前をカンマで区切って並べる）。大文字と小文字を区別せず、
    /// 前後の空白は無視する。<see cref="NoneKeyword"/> だけなら 0 件。OSMODE と同じく値の合計（<see cref="ArtifactKindSet.Value"/>）の数でもよい。知らない名前・空の項目・NONE とほかの名前の混在は、集合を null にして理由を返す。
    /// </summary>
    public static (ArtifactKindSet? Set, string? Error) ParseList(string text)
    {
        if (long.TryParse(text.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out long value))
            return ArtifactKindSet.FromValue(value) is { } fromValue
                ? (fromValue, null)
                : (null, MessageCatalogs.Current.Format("settings.formats_value_range", ArtifactKindSet.Of(All.ToArray()).Value, text.Trim()));
        var items = text.Split(',').Select(item => item.Trim()).ToArray();
        if (items.Length == 1 && string.Equals(items[0], NoneKeyword, StringComparison.OrdinalIgnoreCase))
            return (ArtifactKindSet.Empty, null);
        var set = ArtifactKindSet.Empty;
        foreach (string item in items)
        {
            if (FromName(item.ToLowerInvariant()) is not { } kind)
                return (null, MessageCatalogs.Current.Format("settings.formats_invalid", item, string.Join(",", All.Select(Label)), NoneKeyword));
            set = set.With(kind, true);
        }
        return (set, null);
    }
}

/// <summary>成果物の形式の集合（値として比べられる）。</summary>
public readonly struct ArtifactKindSet : IEquatable<ArtifactKindSet>
{
    readonly int _bits;

    ArtifactKindSet(int bits) => _bits = bits;

    public static ArtifactKindSet Empty => default;

    public static ArtifactKindSet Of(params ArtifactKind[] kinds) =>
        new(kinds.Aggregate(0, (bits, kind) => bits | Bit(kind)));

    public bool Contains(ArtifactKind kind) => (_bits & Bit(kind)) != 0;

    public bool IsEmpty => _bits == 0;

    /// <summary>
    /// OSMODE と同じく、形式ごとの値（PDF=1、SVG=2、EMF=4、PNG=8。列挙の順の 2 の累乗）の合計。-PETAOPT で数でも指定できるようにする。
    /// 利用者とスクリプトとの約束なので、列挙の順を変えない。
    /// </summary>
    public int Value => _bits;

    /// <summary>値の合計から集合を作る。どの形式の値の合計にもならない値（負、または全部の合計より大きい）は null。</summary>
    public static ArtifactKindSet? FromValue(long value) =>
        value >= 0 && value <= Of(ArtifactKinds.All.ToArray())._bits ? new ArtifactKindSet((int)value) : null;

    /// <summary>集合に入っている形式（列挙の順）。</summary>
    public IEnumerable<ArtifactKind> Kinds
    {
        get
        {
            int bits = _bits;
            return ArtifactKinds.All.Where(kind => (bits & Bit(kind)) != 0);
        }
    }

    /// <summary><paramref name="kind"/> を入れた（<paramref name="on"/> が false なら外した）集合。</summary>
    public ArtifactKindSet With(ArtifactKind kind, bool on) => new(on ? _bits | Bit(kind) : _bits & ~Bit(kind));

    public bool Equals(ArtifactKindSet other) => _bits == other._bits;

    public override bool Equals(object? obj) => obj is ArtifactKindSet other && Equals(other);

    public override int GetHashCode() => _bits;

    public override string ToString() => string.Join(",", Kinds.Select(ArtifactKinds.Name));

    static int Bit(ArtifactKind kind) => 1 << (int)kind;
}
