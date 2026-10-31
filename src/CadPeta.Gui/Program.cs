using System;
using System.Windows;

namespace CadPeta.Gui;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new MainWindow();
        // PDF を exe へドロップしたとき・「プログラムから開く」で開いたときは、引数の PDF を開く
        if (args.Length > 0) window.Loaded += (_, _) => window.OpenPdf(args[0]);
        app.Run(window);
    }
}
