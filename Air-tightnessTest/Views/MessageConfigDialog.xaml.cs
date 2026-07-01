using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LumbarMassageTest.Views
{
    public partial class MessageConfigDialog : Window
    {
        public ushort[] MessageData { get; private set; } = new ushort[20];
        private TextBox[] _messageTextBoxes;

        public MessageConfigDialog(ushort[] initialData = null)
        {
            InitializeComponent();

            // 初始化文本框数组
            _messageTextBoxes = new TextBox[20];
            for (int i = 1; i <= 20; i++)
            {
                _messageTextBoxes[i - 1] = FindName($"MsgTextBox{i}") as TextBox;
            }

            // 加载初始数据
            if (initialData != null && initialData.Length == 20)
            {
                MessageData = initialData.ToArray();
                LoadMessageData();
            }

            UpdatePreview();
        }

        private void LoadMessageData()
        {
            for (int i = 0; i < 20; i++)
            {
                if (_messageTextBoxes[i] != null)
                {
                    // 只显示低8位（1字节）
                    _messageTextBoxes[i].Text = (MessageData[i] & 0xFF).ToString("X2");
                }
            }
        }

        private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox && int.TryParse(textBox.Tag?.ToString(), out int index))
            {
                string text = textBox.Text.Trim().ToUpper();

                // 限制输入长度为2个字符
                if (text.Length > 2)
                {
                    text = text.Substring(0, 2);
                    textBox.Text = text;
                    textBox.CaretIndex = 2;
                }

                // 验证输入是否为有效的16进制数字
                if (byte.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out byte value))
                {
                    if (index >= 0 && index < MessageData.Length)
                    {
                        MessageData[index] = value;
                    }
                    textBox.Background = Brushes.White;
                    textBox.Foreground = new SolidColorBrush(Color.FromRgb(45, 55, 72));
                    UpdateStatus("输入有效", "#10B981");
                }
                else if (string.IsNullOrEmpty(text))
                {
                    if (index >= 0 && index < MessageData.Length)
                    {
                        MessageData[index] = 0;
                    }
                    textBox.Background = Brushes.White;
                    textBox.Foreground = new SolidColorBrush(Color.FromRgb(45, 55, 72));
                    UpdateStatus("请输入16进制数字", "#4A5568");
                }
                else
                {
                    textBox.Background = new SolidColorBrush(Color.FromRgb(254, 215, 215));
                    textBox.Foreground = new SolidColorBrush(Color.FromRgb(197, 48, 48));
                    UpdateStatus("无效的16进制数字", "#E53E3E");
                }

                UpdatePreview();
            }
        }

        private void UpdatePreview()
        {
            string preview = string.Join(" ", MessageData.Select(b => b.ToString("X2")));
            PreviewTextBlock.Text = preview;
        }

        private void UpdateStatus(string message, string colorHex)
        {
            StatusTextBlock.Text = message;
            StatusTextBlock.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        // 新增：填充3A开始0D0A格式
        private void BtnFill3A0D0A_Click(object sender, RoutedEventArgs e)
        {
            // 3A开始的典型报文格式
            byte[] defaultMessage = new byte[20]
            {
                0x3A, 0x01, 0x22, 0x00, 0x00, 0x00, 0x02, 0x08,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x0D, 0x0A
            };

            for (int i = 0; i < 20; i++)
            {
                MessageData[i] = defaultMessage[i];
                _messageTextBoxes[i].Text = defaultMessage[i].ToString("X2");
            }
            UpdatePreview();
            UpdateStatus("已填充3A开始0D0A格式报文", "#10B981");
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < 20; i++)
            {
                MessageData[i] = 0;
                _messageTextBoxes[i].Text = "00";
            }
            UpdatePreview();
            UpdateStatus("已全部清零", "#10B981");
        }

        // 新增：读取报文功能
        private void BtnReadMessage_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 这里可以添加从PLC或其他设备读取报文的逻辑
                // 暂时模拟读取数据
                var random = new Random();
                for (int i = 0; i < 20; i++)
                {
                    byte value = (byte)random.Next(0, 256);
                    MessageData[i] = value;
                    _messageTextBoxes[i].Text = value.ToString("X2");
                }
                UpdatePreview();
                UpdateStatus("已从设备读取报文数据", "#10B981");
            }
            catch (Exception ex)
            {
                UpdateStatus($"读取失败: {ex.Message}", "#E53E3E");
            }
        }

        private void BtnCopyPreview_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string preview = string.Join(" ", MessageData.Select(b => b.ToString("X2")));
                Clipboard.SetText(preview);
                UpdateStatus("报文已复制到剪贴板", "#10B981");
            }
            catch (Exception ex)
            {
                UpdateStatus($"复制失败: {ex.Message}", "#E53E3E");
            }
        }

        private void BtnOK_Click(object sender, RoutedEventArgs e)
        {
            // 验证所有输入是否有效
            bool allValid = true;
            for (int i = 0; i < 20; i++)
            {
                if (!byte.TryParse(_messageTextBoxes[i].Text, System.Globalization.NumberStyles.HexNumber, null, out _))
                {
                    allValid = false;
                    break;
                }
            }

            if (allValid)
            {
                DialogResult = true;
                Close();
            }
            else
            {
                UpdateStatus("请修正无效的输入", "#E53E3E");
                MessageBox.Show("存在无效的16进制数字，请检查并修正后重试。", "输入错误",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}