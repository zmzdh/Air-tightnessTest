using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace AudioActuatorCanTest.UserControls
{
    public partial class HelpControl : UserControl
    {
        private static readonly IReadOnlyList<string> CandidateRelativePaths = new[]
        {
            Path.Combine("Docs", "UsageHelp.txt"),
            "UsageHelp.txt"
        };

        public HelpControl()
        {
            InitializeComponent();
            Loaded += HelpControl_Loaded;
        }

        private void HelpControl_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= HelpControl_Loaded;
            LoadHelpContent();
        }

        private void BtnReload_OnClick(object sender, RoutedEventArgs e)
        {
            LoadHelpContent();
        }

        private void LoadHelpContent()
        {
            try
            {
                var helpPath = ResolveHelpFilePath();
                if (helpPath == null || !File.Exists(helpPath))
                {
                    TxtHelpContent.Text = "未找到使用帮助文件。请联系系统管理员。";
                    return;
                }

                TxtHelpContent.Text = File.ReadAllText(helpPath);
            }
            catch (Exception ex)
            {
                TxtHelpContent.Text = $"加载使用帮助失败: {ex.Message}";
            }
        }

        private static string? ResolveHelpFilePath()
        {
            var baseDirectories = new List<string>
            {
                AppContext.BaseDirectory,
                AppDomain.CurrentDomain.BaseDirectory ?? string.Empty,
            };

            // 在开发环境中，帮助文件位于项目根目录
            baseDirectories.Add(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..")));

            foreach (var baseDir in baseDirectories.Where(d => !string.IsNullOrWhiteSpace(d)))
            {
                foreach (var relative in CandidateRelativePaths)
                {
                    var combined = Path.GetFullPath(Path.Combine(baseDir, relative));
                    if (File.Exists(combined))
                    {
                        return combined;
                    }
                }
            }

            return null;
        }
    }
}
