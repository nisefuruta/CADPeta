using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Itjp.Ijcad.ApplicationServices.Core;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.EditorInput;
using Itjp.Ijcad.Runtime;

[assembly: ExtensionApplication(typeof(CadPeta.Tools.IjcadProbe.ProbeApp))]
[assembly: CommandClass(typeof(CadPeta.Tools.IjcadProbe.ProbeCommands))]

namespace CadPeta.Tools.IjcadProbe
{
    /// <summary>
    /// 読込確認プローブ。ロード直後（Initialize）にマーカーファイルを書く。
    /// コマンド登録に失敗してもロード自体の成否がファイルで判定できるようにする。
    /// </summary>
    public sealed class ProbeApp : IExtensionApplication
    {
        internal static readonly string LogDir =
            Path.Combine(Path.GetTempPath(), "cadpeta_p3ijcad");

        public void Initialize()
        {
            try
            {
                Directory.CreateDirectory(LogDir);
                var sb = new StringBuilder();
                sb.AppendLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} Initialize 到達");
                sb.AppendLine($"AppDomain={AppDomain.CurrentDomain.FriendlyName}");
                sb.AppendLine($"CLR={Environment.Version}");
                sb.AppendLine($"Process={Process()}");
                sb.AppendLine($"GcDbMgd={typeof(Entity).Assembly.Location}");
                sb.AppendLine($"GcCoreMgd={typeof(Application).Assembly.Location}");
                File.AppendAllText(Path.Combine(LogDir, "probe_load.log"), sb.ToString(), Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                // ログ先自体が作れない場合の最終手段（%TEMP% 直下）
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "cadpeta_p3ijcad_init_error.log"),
                        ex.ToString());
                }
                catch
                {
                    // 報告手段が尽きたときのみ握らない選択肢がない（ホストを落とさない）
                }
            }
        }

        public void Terminate()
        {
        }

        private static string Process()
        {
            using (var p = System.Diagnostics.Process.GetCurrentProcess())
            {
                return $"{p.ProcessName} (PID {p.Id}), MainModule={p.MainModule?.FileName}";
            }
        }
    }

    public sealed class ProbeCommands
    {
        /// <summary>
        /// 実行環境・API 面の存在確認。結果はログファイルとコマンドウィンドウの両方へ出す。
        /// 型・メンバーの存在確認はリフレクションで行い、不在でもプローブ全体は完走する。
        /// </summary>
        [CommandMethod("PETAPROBE")]
        public void PetaProbe()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed = doc?.Editor;
            var sb = new StringBuilder();
            sb.AppendLine($"=== PETAPROBE {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} ===");
            try
            {
                sb.AppendLine($"Document={doc?.Name ?? "(null)"}");
                sb.AppendLine($"Version={Application.Version}");
                TryAppendSysVar(sb, "SECURELOAD");
                TryAppendSysVar(sb, "FILEDIA");
                TryAppendSysVar(sb, "PRODUCT");
                TryAppendSysVar(sb, "ACADVER");
                TryAppendSysVar(sb, "DWGNAME");
                TryAppendSysVar(sb, "MEASUREMENT");
                TryAppendSysVar(sb, "BACKGROUNDPLOT");
                TryAppendSysVar(sb, "TILEMODE");
                TryAppendSysVar(sb, "CVPORT");

                if (doc != null)
                {
                    var db = doc.Database;
                    using (var tr = db.TransactionManager.StartTransaction())
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                        var ms = (BlockTableRecord)tr.GetObject(
                            bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                        int count = 0;
                        var kinds = new System.Collections.Generic.SortedDictionary<string, int>();
                        foreach (ObjectId id in ms)
                        {
                            count++;
                            var key = id.ObjectClass?.Name ?? "(unknown)";
                            kinds[key] = kinds.TryGetValue(key, out var n) ? n + 1 : 1;
                        }
                        sb.AppendLine($"ModelSpace entities={count}");
                        foreach (var kv in kinds)
                        {
                            sb.AppendLine($"  {kv.Key} x{kv.Value}");
                        }
                        tr.Commit();
                    }
                }

                // アダプタ移植で使う API の存在確認（リフレクション。不在でも完走する）
                sb.AppendLine("--- API presence ---");
                var tdMgd = typeof(Entity).Assembly;
                var fxCore = typeof(Application).Assembly;
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.DrawOrderTable");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.Wipeout");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.MText");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.Spline");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.Hatch");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.Ole2Frame");
                CheckType(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.RasterImage");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.MText", "ExplodeFragments");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeIsUcsOrientedAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeIsUprightAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeNumberAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeScaleAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeRotationAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeOffsetAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "TextAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.LinetypeTableRecord", "ShapeStyleAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.TextStyleTableRecord", "FileName");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.TextStyleTableRecord", "BigFontFileName");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.Polyline", "GetBulgeAt");
                CheckMember(sb, tdMgd, "Itjp.Ijcad.DatabaseServices.HostApplicationServices", "FindFile");
                CheckType(sb, fxCore, "Itjp.Ijcad.EditorInput.PromptSelectionOptions");
                CheckMember(sb, fxCore, "Itjp.Ijcad.EditorInput.Editor", "SelectImplied");
                CheckMember(sb, fxCore, "Itjp.Ijcad.EditorInput.Editor", "GetSelection");
                CheckMember(sb, fxCore, "Itjp.Ijcad.EditorInput.Editor", "GetKeywords");
                CheckMember(sb, fxCore, "Itjp.Ijcad.EditorInput.Editor", "GetPoint");
                CheckType(sb, fxCore, "Itjp.Ijcad.PlottingServices.PlotEngine");
                CheckType(sb, fxCore, "Itjp.Ijcad.PlottingServices.PlotConfigManager");
                DumpPlotDevices(sb, fxCore, "Itjp.Ijcad.PlottingServices.PlotConfigManager");

                sb.AppendLine("PETAPROBE 完了（例外なし）");
            }
            catch (System.Exception ex)
            {
                sb.AppendLine("PETAPROBE 例外:");
                sb.AppendLine(ex.ToString());
            }

            var text = sb.ToString();
            var path = Path.Combine(ProbeApp.LogDir, "probe_cmd.log");
            try
            {
                Directory.CreateDirectory(ProbeApp.LogDir);
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch (System.Exception ex)
            {
                ed?.WriteMessage($"\nログ書き込み失敗: {ex}\n");
            }
            ed?.WriteMessage($"\n{text}\nログ: {path}\n");
        }

        private static void TryAppendSysVar(StringBuilder sb, string name)
        {
            try
            {
                sb.AppendLine($"{name}={Application.GetSystemVariable(name) ?? "(null)"}");
            }
            catch (System.Exception ex)
            {
                sb.AppendLine($"{name}=(取得失敗: {ex.GetType().Name}: {ex.Message})");
            }
        }

        private static void CheckType(StringBuilder sb, Assembly asm, string fullName)
        {
            var t = asm.GetType(fullName, throwOnError: false);
            sb.AppendLine($"{fullName}: {(t != null ? "あり" : "なし")}");
        }

        private static void CheckMember(StringBuilder sb, Assembly asm, string fullName, string member)
        {
            var t = asm.GetType(fullName, throwOnError: false);
            if (t == null)
            {
                sb.AppendLine($"{fullName}.{member}: 型なし");
                return;
            }
            var found = t.GetMember(member,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static).Any();
            sb.AppendLine($"{fullName}.{member}: {(found ? "あり" : "なし")}");
        }

        private static void DumpPlotDevices(StringBuilder sb, Assembly asm, string typeName)
        {
            sb.AppendLine("--- plot devices ---");
            var t = asm.GetType(typeName, false);
            try
            {
                var devices = t?.GetProperty("Devices", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
                if (devices != null) foreach (var device in devices) sb.AppendLine($"DEVICE {device}");
            }
            catch (System.Exception ex) { sb.AppendLine($"DEVICE列挙失敗: {ex}"); }
        }
    }
}
