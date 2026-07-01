using System.Collections;
using System.Windows;
using System.Windows.Controls;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Views.AutoTest
{
    public partial class MassageTestView : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable), typeof(MassageTestView), new PropertyMetadata(null));

        public static readonly DependencyProperty SettingsProperty = DependencyProperty.Register(
            nameof(Settings), typeof(MassageTestSettings), typeof(MassageTestView),
            new PropertyMetadata(new MassageTestSettings()));

        public MassageTestView()
        {
            InitializeComponent();
        }

        public IEnumerable ItemsSource
        {
            get => (IEnumerable)GetValue(ItemsSourceProperty);
            set => SetValue(ItemsSourceProperty, value);
        }

        public MassageTestSettings Settings
        {
            get => (MassageTestSettings)GetValue(SettingsProperty);
            set => SetValue(SettingsProperty, value);
        }

        public DataGrid ConfigGrid => MassageConfigGrid;


        public void ApplyChannelColumnVisibility(int channelCount)
        {
            bool showCh3 = channelCount >= 3;
            bool showCh4 = channelCount >= 4;

            foreach (var column in MassageConfigGrid.Columns)
            {
                string header = column.Header?.ToString() ?? string.Empty;
                if (header.Contains("通道3"))
                {
                    column.Visibility = showCh3 ? Visibility.Visible : Visibility.Collapsed;
                }
                else if (header.Contains("通道4"))
                {
                    column.Visibility = showCh4 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
        }

        public object SelectedMassageConfig => MassageConfigGrid.SelectedItem;
    }
}
