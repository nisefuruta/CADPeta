using System;
using System.ComponentModel;
using System.Globalization;

namespace CadPeta.UseCases;

/// <summary>
/// PETAOPT のダイアログの入力（表示モデル）。今の設定から始め、項目ごとに使えない値の理由を返し、すべて使えるときだけ設定にする。
/// 検証は <see cref="CadPetaSettings"/> と <see cref="FileNameTemplate"/> のもの（-PETAOPT と設定のファイルの読み込みと同じ判定）。
/// 画面は値の変更を <see cref="PropertyChanged"/> で受け取る。
/// </summary>
public sealed class SettingsForm : INotifyPropertyChanged
{
    ArtifactKindSet _formats;
    string _outputFolder;
    string _fileName;
    string _pngDpi;
    readonly double _originalPngDpi;

    public SettingsForm(CadPetaSettings settings)
    {
        _originalPngDpi = settings.PngDpi;
        _formats = settings.Formats;
        _outputFolder = settings.OutputFolder;
        _fileName = settings.FileName.Text;
        _pngDpi = settings.PngDpi.ToString(CultureInfo.InvariantCulture);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsOn(ArtifactKind kind) => _formats.Contains(kind);

    public void SetOn(ArtifactKind kind, bool on)
    {
        _formats = _formats.With(kind, on);
        Notify(nameof(IsOn), nameof(PngDpiError));
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set { _outputFolder = value; Notify(nameof(OutputFolder), nameof(OutputFolderError)); }
    }

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; Notify(nameof(FileName), nameof(FileNameError)); }
    }

    /// <summary>PNG の解像度（入力の文字列。数でなければ <see cref="PngDpiError"/> に理由が入る）。</summary>
    public string PngDpi
    {
        get => _pngDpi;
        set { _pngDpi = value; Notify(nameof(PngDpi), nameof(PngDpiError)); }
    }

    public string? OutputFolderError => CadPetaSettings.ValidateOutputFolder(_outputFolder);

    /// <summary>
    /// 出力先が完全なパスならそのパス、相対パスか使えない値なら null（フォルダを選ぶ画面の最初のフォルダに使う）。
    /// 検証を先に行うので、.NET Framework の Path の関数が例外を投げる文字を含む値は渡さない。
    /// </summary>
    public string? FullOutputFolder =>
        OutputFolderError == null && CadPetaSettings.IsFullyQualified(_outputFolder) ? _outputFolder : null;

    public string? FileNameError => FileNameTemplate.Validate(_fileName);

    // 数でない入力は、範囲の外と同じ理由（使える範囲）を返す。PNG がオフなら画面の欄は使えず直せないので、検証しない
    public string? PngDpiError => IsOn(ArtifactKind.Png) ? CadPetaSettings.ParsePngDpi(_pngDpi, out _) : null;

    /// <summary>
    /// すべての項目が使えるときは設定、1 つでも使えなければ null。PNG がオフで解像度の入力が使えなければ、元の解像度を保存する。
    /// </summary>
    public CadPetaSettings? ToSettings() =>
        OutputFolderError == null && FileNameError == null && PngDpiError == null
            ? new CadPetaSettings(_outputFolder, FileNameTemplate.Parse(_fileName).Template!, _formats,
                CadPetaSettings.ParsePngDpi(_pngDpi, out double dpi) == null ? dpi : _originalPngDpi)
            : null;

    /// <summary>ファイル名の例（形に例の値を差し込み、EMF の拡張子を付ける）。形が使えなければ空。</summary>
    public string Example(string drawing, string layout, DateTime now) =>
        FileNameTemplate.Parse(_fileName).Template is { } template
            ? template.Expand(new FileNameFields(drawing, layout, now, MessageCatalogs.Current.Format("settings.example_comment")), seq: 1)
              + ArtifactKinds.Extension(ArtifactKind.Emf)
            : "";

    void Notify(params string[] names)
    {
        foreach (string name in names)
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}