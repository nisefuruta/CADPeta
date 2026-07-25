// 診断ログ: 非可逆処理・規則的な正規化の「件数と対象」を記録する。
// 内核に属し、外部ライブラリ参照を持たない。
namespace CadPeta.Ir;

public sealed class IrDiagnostics
{
    public sealed record Entry(string Type, string Target, double? Value = null);

    public List<Entry> Entries = new();

    public void Add(string type, string target) => Entries.Add(new Entry(type, target));

    public void Add(string type, string target, double value) =>
        Entries.Add(new Entry(type, target, value));

    /// <summary>種別ごとの件数と対象一覧（処理ログ形式）。</summary>
    public IEnumerable<(string Type, int Count, IReadOnlyList<string> Targets)> Summarize() =>
        Entries.GroupBy(e => e.Type)
               .Select(g => (g.Key, g.Count(), (IReadOnlyList<string>)g.Select(e => e.Target).ToList()));
}
