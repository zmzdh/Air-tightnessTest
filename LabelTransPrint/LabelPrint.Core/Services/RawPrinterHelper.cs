using System.ComponentModel;
using System.Runtime.InteropServices;

namespace LabelTransPrint.Services;

/// <summary>
/// 备用原始打印类：后续若切换纯 PPLB 指令打印，可直接复用。
/// </summary>
public static class RawPrinterHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private class DOCINFOW
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDocName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pOutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pDataType;
    }

    [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOW di);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.Drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

    public static void SendStringToPrinter(string printerName, string data)
    {
        IntPtr hPrinter = IntPtr.Zero;
        IntPtr pBytes = IntPtr.Zero;

        try
        {
            if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenPrinter 失败");

            var doc = new DOCINFOW { pDocName = "RAW LABEL", pDataType = "RAW" };
            if (!StartDocPrinter(hPrinter, 1, doc))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartDocPrinter 失败");

            if (!StartPagePrinter(hPrinter))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "StartPagePrinter 失败");

            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(data);
            pBytes = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, pBytes, bytes.Length);

            if (!WritePrinter(hPrinter, pBytes, bytes.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WritePrinter 失败");

            EndPagePrinter(hPrinter);
            EndDocPrinter(hPrinter);
        }
        finally
        {
            if (pBytes != IntPtr.Zero) Marshal.FreeCoTaskMem(pBytes);
            if (hPrinter != IntPtr.Zero) ClosePrinter(hPrinter);
        }
    }
}
