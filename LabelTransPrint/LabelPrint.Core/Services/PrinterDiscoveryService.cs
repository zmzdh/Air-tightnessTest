using System.Drawing.Printing;

namespace LabelTransPrint.Services;

public static class PrinterDiscoveryService
{
    public static List<string> GetInstalledPrinters()
    {
        return PrinterSettings.InstalledPrinters
            .Cast<string>()
            .OrderBy(x => x)
            .ToList();
    }
}
