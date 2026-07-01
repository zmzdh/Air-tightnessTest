using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace AudioActuatorCanTest
{
    internal static class ConsoleWindowManager
    {
        private const int SW_HIDE = 0;
        private const int ERROR_INVALID_HANDLE = 6;
        private static int _initialized;

        [ModuleInitializer]
        internal static void Initialize() => EnsureConsoleDetached();

        internal static void EnsureConsoleDetached()
        {
            if (Interlocked.Exchange(ref _initialized, 1) == 1)
            {
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            var consoleHandle = GetConsoleWindow();
            if (consoleHandle != IntPtr.Zero)
            {
                ShowWindow(consoleHandle, SW_HIDE);
            }

            if (!FreeConsole())
            {
                var lastError = Marshal.GetLastWin32Error();
                if (consoleHandle != IntPtr.Zero && lastError != ERROR_INVALID_HANDLE)
                {
                    ShowWindow(consoleHandle, SW_HIDE);
                }
            }

            SuppressConsoleIO();
        }

        private static void SuppressConsoleIO()
        {
            TrySetOut(TextWriter.Null);
            TrySetError(TextWriter.Null);
            TrySetIn(TextReader.Null);
        }

        private static void TrySetOut(TextWriter writer)
        {
            try
            {
                Console.SetOut(writer);
            }
            catch
            {
                // Ignored
            }
        }

        private static void TrySetError(TextWriter writer)
        {
            try
            {
                Console.SetError(writer);
            }
            catch
            {
                // Ignored
            }
        }

        private static void TrySetIn(TextReader reader)
        {
            try
            {
                Console.SetIn(reader);
            }
            catch
            {
                // Ignored
            }
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();
    }
}
