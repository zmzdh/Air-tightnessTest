using System;
using System.Windows;

namespace AudioActuatorCanTest
{
    public static class EntryPoint
    {
        [STAThread]
        public static void Main()
        {
            ConsoleWindowManager.EnsureConsoleDetached();

            var app = new App();
            app.InitializeComponent();
            app.Run();
        }
    }
}
