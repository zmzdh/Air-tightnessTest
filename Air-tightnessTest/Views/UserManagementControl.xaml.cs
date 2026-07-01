using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LumbarMassageTest.Models;
using LumbarMassageTest.Services;

namespace LumbarMassageTest.UserControls
{
    public partial class UserManagementControl : UserControl
    {
        private readonly DatabaseService _dbService;
        private readonly ILogService _logService;
        private List<User> _users;
        private User? _currentUser;
        private bool _isEditing = false;

        public UserManagementControl(DatabaseService dbService)
            : this(dbService, LogService.Instance)
        {
        }

        public UserManagementControl(DatabaseService dbService, ILogService? logService)
        {
            InitializeComponent();
            _dbService = dbService ?? throw new ArgumentNullException(nameof(dbService));
            _logService = logService ?? LogService.Instance;

            Loaded += async (s, e) =>
            {
                // 确保有当前登录用户
                if (DatabaseService.CurrentUser == null)
                {
                    MessageBox.Show("请先登录系统", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 检查用户权限
                if (DatabaseService.CurrentUser.Role != "Admin" &&
                    DatabaseService.CurrentUser.Role != "Engineer")
                {
                    MessageBox.Show("您没有权限访问用户管理功能", "权限不足",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    DisableAllControls();
                    return;
                }

                await LoadUsersAsync();
                ClearUserDetail();

                // 设置初始按钮状态
                UpdateControlsState();
            };
        }

        private async Task LoadUsersAsync()
        {
            try
            {
                _users = await _dbService.GetAllUsersAsync();
                UsersDataGrid.ItemsSource = _users;

                // 确保UI状态正确更新
                UpdateControlsState();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载用户列表失败: {ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadUserStatisticsAsync(User user)
        {
            try
            {
                var stats = await _dbService.GetUserStatisticsAsync(user.Username);
                TxtLoginCount.Text = stats.LoginCount.ToString();
                TxtTestCount.Text = stats.TestCount.ToString();
            }
            catch (Exception ex)
            {
                TxtLoginCount.Text = "0";
                TxtTestCount.Text = "0";
                _logService.LogError("加载用户统计失败", ex);
            }
        }

        private async void UsersDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UsersDataGrid.SelectedItem is User selectedUser)
            {
                _currentUser = selectedUser;
                LoadUserDetail(selectedUser);
                await LoadUserStatisticsAsync(selectedUser);
                _isEditing = false;
                UpdateControlsState();
            }
        }

        private void LoadUserDetail(User user)
        {
            if (user == null)
            {
                ClearUserDetail();
                return;
            }

            TxtUsername.Text = user.Username;
            TxtFullName.Text = user.FullName;
            TxtEmail.Text = user.Email ?? "";
            ChkIsActive.IsChecked = user.IsActive;

            // 设置角色
            foreach (ComboBoxItem item in CmbRole.Items)
            {
                if (item.Tag?.ToString() == user.Role)
                {
                    CmbRole.SelectedItem = item;
                    break;
                }
            }

            // 设置部门
            CmbDepartment.Text = user.Department ?? "";

            // 设置权限
            SetUserPermissions(user);

            // 设置统计信息
            TxtCreatedAt.Text = user.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss");
            TxtLastLoginAt.Text = user.LastLoginAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "从未登录";

            // 清空密码框
            PwdNew.Clear();
            PwdConfirm.Clear();

            // 根据当前登录用户权限设置控件状态
            UpdateControlsState();
        }

        private void SetUserPermissions(User user)
        {
            // 根据角色设置默认权限
            switch (user.Role)
            {
                case "Admin":
                    ChkCanTest.IsChecked = true;
                    ChkCanManual.IsChecked = true;
                    ChkCanConfig.IsChecked = true;
                    ChkCanReport.IsChecked = true;
                    ChkCanUserManage.IsChecked = true;
                    ChkCanSystemSettings.IsChecked = true;
                    break;
                case "Engineer":
                    ChkCanTest.IsChecked = true;
                    ChkCanManual.IsChecked = true;
                    ChkCanConfig.IsChecked = true;
                    ChkCanReport.IsChecked = true;
                    ChkCanUserManage.IsChecked = false;
                    ChkCanSystemSettings.IsChecked = false;
                    break;
                case "Operator":
                    ChkCanTest.IsChecked = true;
                    ChkCanManual.IsChecked = false;
                    ChkCanConfig.IsChecked = false;
                    ChkCanReport.IsChecked = true;
                    ChkCanUserManage.IsChecked = false;
                    ChkCanSystemSettings.IsChecked = false;
                    break;
            }

            // 如果用户有自定义权限，这里可以从数据库加载
        }

        private void UpdateControlsState()
        {
            var currentLoginUser = DatabaseService.CurrentUser;
            if (currentLoginUser == null)
            {
                _logService.LogWarning("没有当前登录用户，禁用所有控件");
                DisableAllControls();
                return;
            }

            // 检查当前用户是否有管理员权限
            bool isAdmin = currentLoginUser.Role == "Admin";
            bool isEngineer = currentLoginUser.Role == "Engineer";

            // 添加用户按钮始终对管理员可用
            BtnAddUser.IsEnabled = isAdmin;

            // 编辑、删除和重置密码按钮根据选中用户和权限决定
            bool hasSelectedUser = _currentUser != null;
            bool canEditSelectedUser = hasSelectedUser &&
                                     (isAdmin || (isEngineer && _currentUser.Role != "Admin"));

            BtnEditUser.IsEnabled = canEditSelectedUser;
            BtnDeleteUser.IsEnabled = canEditSelectedUser && _currentUser.Id != currentLoginUser.Id;
            BtnResetPassword.IsEnabled = canEditSelectedUser;

            // 详情区域控件的状态
            TxtUsername.IsEnabled = _isEditing && canEditSelectedUser && (_currentUser?.Id == 0);
            TxtFullName.IsEnabled = _isEditing && canEditSelectedUser;
            CmbRole.IsEnabled = _isEditing && canEditSelectedUser && isAdmin;
            CmbDepartment.IsEnabled = _isEditing && canEditSelectedUser;
            TxtEmail.IsEnabled = _isEditing && canEditSelectedUser;
            ChkIsActive.IsEnabled = _isEditing && canEditSelectedUser;

            // 权限设置只有管理员可以修改
            var permissionControls = new[] { ChkCanTest, ChkCanManual, ChkCanConfig,
                ChkCanReport, ChkCanUserManage, ChkCanSystemSettings };
            foreach (var control in permissionControls)
            {
                control.IsEnabled = _isEditing && isAdmin;
            }

            PwdNew.IsEnabled = _isEditing && canEditSelectedUser;
            PwdConfirm.IsEnabled = _isEditing && canEditSelectedUser;

            BtnSaveUser.IsEnabled = _isEditing && canEditSelectedUser;
            BtnCancelEdit.IsEnabled = _isEditing;
        }

        private void DisableAllControls()
        {
            // 禁用所有输入控件
            TxtUsername.IsEnabled = false;
            TxtFullName.IsEnabled = false;
            CmbRole.IsEnabled = false;
            CmbDepartment.IsEnabled = false;
            TxtEmail.IsEnabled = false;
            ChkIsActive.IsEnabled = false;

            var permissionControls = new[] { ChkCanTest, ChkCanManual, ChkCanConfig,
                ChkCanReport, ChkCanUserManage, ChkCanSystemSettings };
            foreach (var control in permissionControls)
            {
                control.IsEnabled = false;
            }

            PwdNew.IsEnabled = false;
            PwdConfirm.IsEnabled = false;

            // 禁用所有按钮
            BtnSaveUser.IsEnabled = false;
            BtnCancelEdit.IsEnabled = false;
            BtnAddUser.IsEnabled = false;
            BtnEditUser.IsEnabled = false;
            BtnDeleteUser.IsEnabled = false;
            BtnResetPassword.IsEnabled = false;
        }

        private void ClearUserDetail()
        {
            TxtUsername.Clear();
            TxtFullName.Clear();
            TxtEmail.Clear();
            ChkIsActive.IsChecked = false;
            CmbRole.SelectedIndex = -1;
            CmbDepartment.Text = "";

            var permissionControls = new[] { ChkCanTest, ChkCanManual, ChkCanConfig,
                ChkCanReport, ChkCanUserManage, ChkCanSystemSettings };
            foreach (var control in permissionControls)
            {
                control.IsChecked = false;
            }

            PwdNew.Clear();
            PwdConfirm.Clear();

            TxtCreatedAt.Text = "";
            TxtLastLoginAt.Text = "";
            TxtLoginCount.Text = "";
            TxtTestCount.Text = "";

            _currentUser = null;
            _isEditing = false;
            UpdateControlsState();
        }

        private void BtnAddUser_Click(object sender, RoutedEventArgs e)
        {
            _currentUser = new User
            {
                Id = 0,
                IsActive = true,
                CreatedAt = DateTime.Now,
                Role = "Operator"
            };

            LoadUserDetail(_currentUser);
            _isEditing = true;
            UpdateControlsState();
            TxtUsername.Focus();
        }

        private void BtnEditUser_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUser == null)
            {
                MessageBox.Show("请先选择要编辑的用户", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _isEditing = true;
            UpdateControlsState();
            TxtFullName.Focus();
        }

        private async void BtnDeleteUser_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUser == null)
            {
                MessageBox.Show("请先选择要删除的用户", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要删除用户 '{_currentUser.FullName}({_currentUser.Username})' 吗？\n注意：此操作不可恢复！",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var success = await _dbService.DeleteUserAsync(_currentUser.Id);
                    if (success)
                    {
                        MessageBox.Show("删除成功!", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                        await LoadUsersAsync();
                        ClearUserDetail();
                    }
                    else
                    {
                        MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"删除用户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnResetPassword_Click(object sender, RoutedEventArgs e)
        {
            if (_currentUser == null)
            {
                MessageBox.Show("请先选择要重置密码的用户", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var result = MessageBox.Show($"确定要重置用户 '{_currentUser.FullName}' 的密码为默认密码(123456)吗？",
                "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    var success = await _dbService.ResetPasswordAsync(_currentUser.Id, "123456");
                    if (success)
                    {
                        MessageBox.Show("密码重置成功! 新密码: 123456", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                        MessageBox.Show("密码重置失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"重置密码失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync();
        }

        private async void BtnSaveUser_Click(object sender, RoutedEventArgs e)
        {
            if (!ValidateUserInput())
                return;

            try
            {
                // 构建用户对象
                var user = BuildUserFromInput();

                bool success;
                if (user.Id == 0)
                {
                    // 新增用户
                    success = await _dbService.CreateUserAsync(user);
                    if (success)
                    {
                        MessageBox.Show("用户创建成功!", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    // 更新用户
                    success = await _dbService.UpdateUserAsync(user);
                    if (success)
                    {
                        MessageBox.Show("用户更新成功!", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }

                if (success)
                {
                    await LoadUsersAsync();
                    _isEditing = false;
                    UpdateControlsState();
                }
                else
                {
                    MessageBox.Show("保存用户失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"保存用户失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnCancelEdit_Click(object sender, RoutedEventArgs e)
        {
            _isEditing = false;
            if (_currentUser?.Id == 0)
            {
                // 取消新增，清空详情
                ClearUserDetail();
            }
            else
            {
                // 取消编辑，重新加载用户详情
                LoadUserDetail(_currentUser);
            }
            UpdateControlsState();
        }

        private bool ValidateUserInput()
        {
            if (string.IsNullOrWhiteSpace(TxtUsername.Text))
            {
                MessageBox.Show("请输入用户名", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtUsername.Focus();
                return false;
            }

            if (string.IsNullOrWhiteSpace(TxtFullName.Text))
            {
                MessageBox.Show("请输入姓名", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                TxtFullName.Focus();
                return false;
            }

            if (CmbRole.SelectedItem == null)
            {
                MessageBox.Show("请选择角色", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                CmbRole.Focus();
                return false;
            }

            // 验证密码
            if (!string.IsNullOrEmpty(PwdNew.Password))
            {
                if (PwdNew.Password != PwdConfirm.Password)
                {
                    MessageBox.Show("两次输入的密码不一致", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PwdConfirm.Focus();
                    return false;
                }

                if (PwdNew.Password.Length < 6)
                {
                    MessageBox.Show("密码长度不能少于6位", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    PwdNew.Focus();
                    return false;
                }
            }
            else if (_currentUser?.Id == 0)
            {
                // 新用户必须设置密码
                MessageBox.Show("新用户必须设置密码", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                PwdNew.Focus();
                return false;
            }

            // 验证邮箱格式
            if (!string.IsNullOrEmpty(TxtEmail.Text))
            {
                if (!IsValidEmail(TxtEmail.Text))
                {
                    MessageBox.Show("邮箱格式不正确", "验证错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                    TxtEmail.Focus();
                    return false;
                }
            }

            return true;
        }

        private bool IsValidEmail(string email)
        {
            try
            {
                var addr = new System.Net.Mail.MailAddress(email);
                return addr.Address == email;
            }
            catch
            {
                return false;
            }
        }

        private User BuildUserFromInput()
        {
            var user = new User
            {
                Id = _currentUser?.Id ?? 0,
                Username = TxtUsername.Text.Trim(),
                FullName = TxtFullName.Text.Trim(),
                Role = (CmbRole.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Operator",
                Department = CmbDepartment.Text.Trim(),
                Email = TxtEmail.Text.Trim(),
                IsActive = ChkIsActive.IsChecked ?? false,
                CreatedAt = _currentUser?.CreatedAt ?? DateTime.Now,
                LastLoginAt = _currentUser?.LastLoginAt
            };

            // 如果输入了新密码，设置密码
            if (!string.IsNullOrEmpty(PwdNew.Password))
            {
                user.PasswordHash = _dbService.GetPasswordHash(PwdNew.Password);
            }
            else if (_currentUser?.Id == 0)
            {
                // 新用户必须设置密码，使用默认密码
                user.PasswordHash = _dbService.GetPasswordHash("123456");
            }
            else
            {
                user.PasswordHash = _currentUser?.PasswordHash;
            }

            return user;
        }
    }

    // 用户统计信息模型
    public class UserStatistics
    {
        public int LoginCount { get; set; }
        public int TestCount { get; set; }
    }
}