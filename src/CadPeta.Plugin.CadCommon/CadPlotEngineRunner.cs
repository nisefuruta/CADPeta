using System;
using System.IO;

#if CADPETA_ACAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.PlottingServices;
using CadApplication = Autodesk.AutoCAD.ApplicationServices.Core.Application;
using CadRuntimeException = Autodesk.AutoCAD.Runtime.Exception;
namespace CadPeta.Plugin.Acad;
#elif CADPETA_ARES
using Teigha.ApplicationServices;
using Teigha.DatabaseServices;
using Teigha.PlottingServices;
using CadApplication = Teigha.ApplicationServices.Application;
using CadRuntimeException = Teigha.Runtime.Exception;
namespace CadPeta.Plugin.Ares;
#elif CADPETA_IJCAD
using Itjp.Ijcad.ApplicationServices;
using Itjp.Ijcad.DatabaseServices;
using Itjp.Ijcad.PlottingServices;
using CadApplication = Itjp.Ijcad.ApplicationServices.Core.Application;
using CadRuntimeException = Itjp.Ijcad.Runtime.Exception;
namespace CadPeta.Plugin.Ijcad;
#elif CADPETA_BRICSCAD
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Bricscad.PlottingServices;
using CadApplication = Bricscad.ApplicationServices.Application;
using CadRuntimeException = Teigha.Runtime.Exception;
namespace CadPeta.Plugin.Bricscad;
#else
#error CAD product symbol is required
#endif
using CadPeta.Plugin.Shared;

/// <summary>4製品で共有する PlotEngine 状態機械と CAD グローバル状態の復元。</summary>
internal static class CadPlotEngineRunner
{
    public static void Publish(Document doc, Database workingDatabase, PlotInfo plotInfo,
        string outputPdfPath, int copyCount, TextWriter log, TextWriter verbose)
    {
        // 全体／窓印刷では IPlotPort.BeginSession も準備中の状態を保護するが、選択印刷は
        // Runner を直接呼ぶ。発行時の再検査と BACKGROUNDPLOT 復元はここでも必須である。
        if (PlotFactory.ProcessPlotState != ProcessPlotState.NotPlotting)
            throw new InvalidOperationException(
                Msg.Format("engine.plot_in_progress", PlotFactory.ProcessPlotState));
        object oldBackgroundPlot = CadApplication.GetSystemVariable("BACKGROUNDPLOT");
        Database oldWorkingDatabase = HostApplicationServices.WorkingDatabase;
        string stage = "BeginPlot";
        try
        {
            CadApplication.SetSystemVariable("BACKGROUNDPLOT", 0);
            HostApplicationServices.WorkingDatabase = workingDatabase;
            using var engine = PlotFactory.CreatePublishEngine();
            engine.BeginPlot(null, null);
            stage = "BeginDocument";
            engine.BeginDocument(plotInfo, doc.Name, null, copyCount, true, outputPdfPath);
            stage = "BeginPage";
            using (var pageInfo = new PlotPageInfo())
                engine.BeginPage(pageInfo, plotInfo, true, null);
            stage = "GenerateGraphics";
            engine.BeginGenerateGraphics(null);
            engine.EndGenerateGraphics(null);
            stage = "EndPage";
            engine.EndPage(null);
            stage = "EndDocument";
            engine.EndDocument(null);
            stage = "EndPlot";
            engine.EndPlot(null);
        }
        catch (CadRuntimeException ex)
        {
            throw new InvalidOperationException(
                Msg.Format("engine.stage_failed", stage, ex.ErrorStatus, outputPdfPath), ex);
        }
        finally
        {
            // WorkingDatabase の復元自体が失敗しても、利用者設定の BACKGROUNDPLOT は必ず戻す。
            // 直列に書くと前者の例外で後者が実行されず、プロット設定を0のまま残す。
            try
            {
                HostApplicationServices.WorkingDatabase = oldWorkingDatabase;
            }
            finally
            {
                CadApplication.SetSystemVariable("BACKGROUNDPLOT", oldBackgroundPlot);
            }
        }
        if (!File.Exists(outputPdfPath))
            throw new IOException(Msg.Format("engine.pdf_missing", outputPdfPath));
        verbose.WriteLine(Msg.Format("engine.pdf_done", outputPdfPath));
    }
}
