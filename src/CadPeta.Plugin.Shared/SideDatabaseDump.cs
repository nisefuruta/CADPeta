using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>選択側DBの観測結果。CAD SDK 型を含まない機械比較用の形式。</summary>
public sealed class SideDatabaseMetadata
{
    public string Observation { get; init; } = "";
    public string CreationMethod { get; init; } = "";
    public int EntityCount { get; init; }
    public IReadOnlyDictionary<string, int> EntityTypes { get; init; } =
        new Dictionary<string, int>();
    public SideDatabaseExtents? Extents { get; init; }
    public int ExtentsUnavailableCount { get; init; }
    public IReadOnlyDictionary<string, string?> View { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string?> Layout { get; init; } =
        new Dictionary<string, string?>();
    public IReadOnlyDictionary<string, string?> PlotSettings { get; init; } =
        new Dictionary<string, string?>();
}

public sealed class SideDatabaseExtents
{
    public double[] Min { get; init; } = Array.Empty<double>();
    public double[] Max { get; init; } = Array.Empty<double>();
}

public enum SideDatabaseSnapshotMismatch
{
    None,
    EntityCount,
    EntityTypes,
    ExtentsAvailability,
    Extents,
}

/// <summary>
/// 元選択と側DBの内容が、座標を含めて同じかを判定する。
/// Wblock に渡す基点は原点なので、extents の平行移動も不一致として扱う。
/// </summary>
public static class SideDatabaseSnapshotComparer
{
    const double RelativeTolerance = 1e-9;

    public static SideDatabaseSnapshotMismatch Compare(
        SideDatabaseMetadata source, SideDatabaseMetadata candidate)
    {
        if (source.EntityCount != candidate.EntityCount)
            return SideDatabaseSnapshotMismatch.EntityCount;
        if (!DictionaryEquals(source.EntityTypes, candidate.EntityTypes))
            return SideDatabaseSnapshotMismatch.EntityTypes;
        if (source.ExtentsUnavailableCount != candidate.ExtentsUnavailableCount ||
            (source.Extents == null) != (candidate.Extents == null))
            return SideDatabaseSnapshotMismatch.ExtentsAvailability;
        if (source.Extents != null && candidate.Extents != null &&
            !ExtentsEqual(source.Extents, candidate.Extents))
            return SideDatabaseSnapshotMismatch.Extents;
        return SideDatabaseSnapshotMismatch.None;
    }

    /// <summary>
    /// 不一致の種類と、元の選択（source）と側DB（side）の値を 1 行で返す。エラーの文に入れ、
    /// どの値がどれだけ違ったかを利用者と試験が確かめられるようにする。言語に依らない形にする。
    /// </summary>
    public static string Describe(SideDatabaseMetadata source, SideDatabaseMetadata candidate)
    {
        var mismatch = Compare(source, candidate);
        return mismatch switch
        {
            SideDatabaseSnapshotMismatch.EntityCount =>
                $"EntityCount source={source.EntityCount} side={candidate.EntityCount}",
            SideDatabaseSnapshotMismatch.EntityTypes =>
                $"EntityTypes source={FormatTypes(source.EntityTypes)} side={FormatTypes(candidate.EntityTypes)}",
            SideDatabaseSnapshotMismatch.ExtentsAvailability =>
                $"ExtentsAvailability source={FormatAvailability(source)} side={FormatAvailability(candidate)}",
            SideDatabaseSnapshotMismatch.Extents =>
                $"Extents source={FormatExtents(source.Extents)} side={FormatExtents(candidate.Extents)}",
            _ => mismatch.ToString(),
        };
    }

    static string FormatTypes(IReadOnlyDictionary<string, int> types) =>
        string.Join(",", types.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => $"{p.Key}:{p.Value}"));

    static string FormatAvailability(SideDatabaseMetadata metadata) =>
        $"unavailable:{metadata.ExtentsUnavailableCount},extents:{(metadata.Extents == null ? "none" : "some")}";

    static string FormatExtents(SideDatabaseExtents? extents) =>
        extents == null ? "none" : $"({FormatPoint(extents.Min)})-({FormatPoint(extents.Max)})";

    static string FormatPoint(double[] point) =>
        string.Join(", ", point.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>
    /// 件数・型・大きさを変えず、全軸が同じ量だけ平行移動した場合だけ、候補を元座標へ
    /// 戻す変位を返す。回転・尺度変更・一部図形だけの移動は受理しない。
    /// </summary>
    public static bool TryGetUniformTranslation(SideDatabaseMetadata source,
        SideDatabaseMetadata candidate, out double[] displacement)
    {
        displacement = Array.Empty<double>();
        if (source.EntityCount != candidate.EntityCount ||
            !DictionaryEquals(source.EntityTypes, candidate.EntityTypes) ||
            source.ExtentsUnavailableCount != candidate.ExtentsUnavailableCount ||
            source.Extents == null || candidate.Extents == null ||
            source.Extents.Min.Length != 3 || source.Extents.Max.Length != 3 ||
            candidate.Extents.Min.Length != 3 || candidate.Extents.Max.Length != 3)
            return false;

        var delta = new double[3];
        for (int i = 0; i < 3; i++)
        {
            delta[i] = source.Extents.Min[i] - candidate.Extents.Min[i];
            double maxDelta = source.Extents.Max[i] - candidate.Extents.Max[i];
            double scale = Math.Max(1d, Math.Max(Math.Abs(delta[i]), Math.Abs(maxDelta)));
            if (Math.Abs(delta[i] - maxDelta) > RelativeTolerance * scale)
                return false;
        }
        displacement = delta;
        return true;
    }

    static bool DictionaryEquals(IReadOnlyDictionary<string, int> left,
        IReadOnlyDictionary<string, int> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
            if (!right.TryGetValue(pair.Key, out int value) || value != pair.Value)
                return false;
        return true;
    }

    static bool ExtentsEqual(SideDatabaseExtents left, SideDatabaseExtents right) =>
        CoordinatesEqual(left.Min, right.Min) && CoordinatesEqual(left.Max, right.Max);

    static bool CoordinatesEqual(double[] left, double[] right)
    {
        if (left.Length != 3 || right.Length != 3) return false;
        for (int i = 0; i < 3; i++)
        {
            double scale = Math.Max(1d, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            if (Math.Abs(left[i] - right[i]) > RelativeTolerance * scale) return false;
        }
        return true;
    }
}

/// <summary>環境変数で明示的に有効化された場合だけ側DB metadata を書き出す。</summary>
public static class SideDatabaseDump
{
    public const string EnvironmentVariable = "CADPETA_DUMP_SIDE_DB";

    public static string? GetStem() =>
        NormalizeStem(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string? NormalizeStem(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value!.Trim();

    public static string GetPath(string stem, string observation) =>
        stem + "." + observation + ".json";

    public static void Write(string stem, SideDatabaseMetadata metadata)
    {
        var path = GetPath(stem, metadata.Observation);
        File.WriteAllText(path, JsonText.Serialize(SideDatabaseJson.From(metadata), indented: true), TextEncodings.Utf8NoBom);
    }

    // 記録のファイルの形。プロパティの名前と順番は解析の道具との約束で、辞書のキーは観測した名前のまま書く
    [DataContract]
    sealed class SideDatabaseJson
    {
        [DataMember(Name = "observation", Order = 1)] public string Observation { get; set; } = "";
        [DataMember(Name = "creationMethod", Order = 2)] public string CreationMethod { get; set; } = "";
        [DataMember(Name = "entityCount", Order = 3)] public int EntityCount { get; set; }
        [DataMember(Name = "entityTypes", Order = 4)] public Dictionary<string, int> EntityTypes { get; set; } = new();
        [DataMember(Name = "extents", Order = 5)] public ExtentsJson? Extents { get; set; }
        [DataMember(Name = "extentsUnavailableCount", Order = 6)] public int ExtentsUnavailableCount { get; set; }
        [DataMember(Name = "view", Order = 7)] public Dictionary<string, string?> View { get; set; } = new();
        [DataMember(Name = "layout", Order = 8)] public Dictionary<string, string?> Layout { get; set; } = new();
        [DataMember(Name = "plotSettings", Order = 9)] public Dictionary<string, string?> PlotSettings { get; set; } = new();

        public static SideDatabaseJson From(SideDatabaseMetadata m) => new()
        {
            Observation = m.Observation,
            CreationMethod = m.CreationMethod,
            EntityCount = m.EntityCount,
            EntityTypes = m.EntityTypes.ToDictionary(p => p.Key, p => p.Value),
            Extents = m.Extents == null ? null : new ExtentsJson { Min = m.Extents.Min, Max = m.Extents.Max },
            ExtentsUnavailableCount = m.ExtentsUnavailableCount,
            View = m.View.ToDictionary(p => p.Key, p => p.Value),
            Layout = m.Layout.ToDictionary(p => p.Key, p => p.Value),
            PlotSettings = m.PlotSettings.ToDictionary(p => p.Key, p => p.Value),
        };
    }

    [DataContract]
    sealed class ExtentsJson
    {
        [DataMember(Name = "min", Order = 1)] public double[] Min { get; set; } = Array.Empty<double>();
        [DataMember(Name = "max", Order = 2)] public double[] Max { get; set; } = Array.Empty<double>();
    }
}
