namespace CadPeta.Plugin.Shared;

public enum SelectionViewportDisposition
{
    Supported,
    ModelSpace,
    Controller,
    Off,
}

public static class SelectionViewportPolicy
{
    public static bool IsController(int number, int paperSpaceControllerNumber) =>
        number == paperSpaceControllerNumber;

    public static SelectionViewportDisposition Classify(bool sourceIsModelSpace,
        int number, bool on, int paperSpaceControllerNumber)
    {
        if (sourceIsModelSpace) return SelectionViewportDisposition.ModelSpace;
        if (IsController(number, paperSpaceControllerNumber))
            return SelectionViewportDisposition.Controller;
        if (!on) return SelectionViewportDisposition.Off;

        // Numberは表示状態ではない。-1（画面外/MAXACTVP超過）や0（未割当）でも、
        // Onの通常Viewportならネイティブプロットの対象として保持する。
        return SelectionViewportDisposition.Supported;
    }
}
