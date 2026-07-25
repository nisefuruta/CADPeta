// 診断ダンプのファイル名規約。合成ルート（CLI・プラグイン）が各段 SVG を
// 書き出す際の命名を一元化する。IO は持たない（純粋な文字列生成。IO は
// 合成ルートに閉じる方針）。ダンプ本体（直列化＋ファイル書き出し＋エラー処理）は各合成ルートが担う。
using System.Globalization;

namespace CadPeta.UseCases;

public static class DiagnosticDump
{
    // 段番号のゼロ埋め桁数。辞書順ソートでブラウザに段の順どおり並ぶよう固定幅にする。
    // 変換層は最大5段のため2桁で桁溢れしない（99段まで整列が保たれる）。
    const int StageOrdinalDigits = 2;

    /// <summary>段ダンプのファイル名 "&lt;base&gt;.stageNN.&lt;name&gt;.svg"。
    /// NN は段番号のゼロ埋め、name は段名（acquired / background-removed / cropped …）。
    /// 段番号と段名と拡張子だけで「どの段の状態か」がファイル名から一目で分かる。</summary>
    public static string FileName(string baseName, int ordinal, string stageName) =>
        $"{baseName}.stage{ordinal.ToString("D" + StageOrdinalDigits, CultureInfo.InvariantCulture)}.{stageName}.svg";
}
