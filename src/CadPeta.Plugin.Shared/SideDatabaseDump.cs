using System.Text;
using System.Text.Json;

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
        File.WriteAllText(path,
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }), new UTF8Encoding(false));
    }
}
