// Views/AddModelDialog.xaml.cs
using System.Windows;

namespace LumbarMassageTest.Views
{
    public partial class AddModelDialog : Window
    {
        public string ModelName { get; private set; }
        public string ModelDescription { get; private set; }

        public AddModelDialog()
        {
            InitializeComponent();
            TxtModelName.Focus();
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtModelName.Text))
            {
                MessageBox.Show("请输入型号名称", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtModelName.Focus();
                return;
            }

            ModelName = TxtModelName.Text.Trim();
            ModelDescription = TxtModelDesc.Text.Trim();

            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
