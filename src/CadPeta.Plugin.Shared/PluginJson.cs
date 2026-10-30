using System.IO;
using System.Runtime.Serialization.Json;
using CadPeta.UseCases;

namespace CadPeta.Plugin.Shared;

/// <summary>
/// プラグインが書く JSON（診断の diag.json、試験の記録、側 DB の記録）を、.NET の標準の DataContractJsonSerializer で直列化する。
/// System.Text.Json は使わない: net48 では、その依存（System.Memory など）が参照する版が PdfPig の依存と食い違い、
/// プラグインは bindingRedirect を持てないので読み込みに失敗した（2026-10-28、AutoCAD 2024 の PETADIAG）。
/// 型は [DataContract] と [DataMember(Name, Order)] で、プロパティの名前と順番を決める。辞書はオブジェクトの形で書く
/// （キーと値の組の配列にしない）。宣言した型と同じ型の値だけを渡し、型情報（"__type"）を書かせない。
/// </summary>
internal static class PluginJson
{
    public static string Serialize<T>(T value, bool indented)
    {
        var serializer = new DataContractJsonSerializer(typeof(T),
            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        using var stream = new MemoryStream();
        using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, TextEncodings.Utf8NoBom,
                   ownsStream: false, indent: indented, indentChars: "  "))
        {
            serializer.WriteObject(writer, value);
        }
        return TextEncodings.Utf8NoBom.GetString(stream.ToArray());
    }
}
