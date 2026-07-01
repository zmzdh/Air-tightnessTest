// Views/LoginDialog.xaml.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AudioActuatorCanTest.Models;
using AudioActuatorCanTest.Services;

namespace AudioActuatorCanTest.Views
{
    public partial class LoginDialog : Window
    {
        private readonly DatabaseService _dbService;
        private readonly UserPreferenceService _preferenceService;
        private IList<string> _usernames = new List<string>();

        public User CurrentUser { get; private set; }

        public LoginDialog(DatabaseService dbService)
        {
            InitializeComponent();
            _dbService = dbService;
            _preferenceService = new UserPreferenceService();

            // 回车键登录
            TxtPassword.KeyDown += TxtPassword_KeyDown;
            CmbUsername.KeyDown += CmbUsername_KeyDown;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                var users = await _dbService.GetAllUsersAsync();
                _usernames = users.Select(u => u.Username).Distinct().ToList();
                CmbUsername.ItemsSource = _usernames;

                var preferredUsername = _preferenceService.Preferences.LastUsername;
                if (!string.IsNullOrWhiteSpace(preferredUsername) && _usernames.Contains(preferredUsername))
                {
                    CmbUsername.Text = preferredUsername;
                }
                else if (_usernames.Count > 0)
                {
                    CmbUsername.SelectedIndex = 0;
                }

                if (!ApplyRememberedPassword(CmbUsername.Text, clearWhenMissing: false))
                {
                    TxtPassword.Password = "123456";
                }

                CmbUsername.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载用户列表失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void TxtPassword_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnLogin_Click(null, null);
            }
        }

        private void CmbUsername_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                TxtPassword.Focus();
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            var username = CmbUsername.Text?.Trim();

            if (string.IsNullOrWhiteSpace(username))
            {
                MessageBox.Show("请输入用户名", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                CmbUsername.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(TxtPassword.Password))
            {
                MessageBox.Show("请输入密码", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtPassword.Focus();
                return;
            }

            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "登录中...";

            try
            {
                CurrentUser = await _dbService.ValidateUserAsync(username, TxtPassword.Password);

                if (CurrentUser != null)
                {
                    CurrentUser.LastLoginAt = DateTime.Now;
                    await _dbService.SaveUserAsync(CurrentUser);
                    _preferenceService.Save(username, TxtPassword.Password, ChkRememberPassword.IsChecked ?? false);
                    DialogResult = true;
                }
                else
                {
                    MessageBox.Show("用户名或密码错误", "登录失败",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtPassword.Clear();
                    TxtPassword.Focus();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"登录异常: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnLogin.IsEnabled = true;
                BtnLogin.Content = "登录";
            }
        }

        private void CmbUsername_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbUsername.SelectedItem is string username)
            {
                ApplyRememberedPassword(username);
            }
        }

        private void CmbUsername_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyRememberedPassword(CmbUsername.Text);
        }

        private bool ApplyRememberedPassword(string? username, bool clearWhenMissing = true)
        {
            if (string.IsNullOrWhiteSpace(username))
            {
                if (clearWhenMissing)
                {
                    TxtPassword.Clear();
                }
                ChkRememberPassword.IsChecked = false;
                return false;
            }

            if (_preferenceService.TryGetPassword(username, out var storedPassword))
            {
                TxtPassword.Password = storedPassword;
                ChkRememberPassword.IsChecked = true;
                return true;
            }

            if (clearWhenMissing)
            {
                TxtPassword.Clear();
            }
            ChkRememberPassword.IsChecked = false;
            return false;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
