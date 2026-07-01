using System.Collections;
using System.Windows;
using System.Windows.Controls;
using AudioActuatorCanTest.Models;

namespace AudioActuatorCanTest.Views.AutoTest
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

        public object SelectedMassageConfig => MassageConfigGrid.SelectedItem;
    }
}
