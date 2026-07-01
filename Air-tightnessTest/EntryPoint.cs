using System;
using System.Windows;

namespace LumbarMassageTest
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
