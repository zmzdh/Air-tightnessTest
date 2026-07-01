// App.xaml.cs (完整版本)
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using AudioActuatorCanTest.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AudioActuatorCanTest
{
    public partial class App : Application
    {
        private static Mutex? _instanceMutex = null!;
        private static bool _ownsMutex;
        public static ServiceProvider ServiceProvider { get; private set; } = null!;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _instanceMutex = new Mutex(true, "AudioActuatorCanTest.SingleInstance", out createdNew);
            _ownsMutex = createdNew;

            if (!createdNew)
            {
                MessageBox.Show("程序已启动。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown();
                return;
            }

            // 设置全局异常处理
            this.DispatcherUnhandledException += App_DispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

            ShutdownMode = ShutdownMode.OnMainWindowClose;

            // 创建必要的目录
            CreateDirectories();

            // 配置依赖注入
            ConfigureServices();

            base.OnStartup(e);
        }

        private void CreateDirectories()
        {
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var directories = new[]
            {
                Path.Combine(baseDir, "Config"),
                Path.Combine(baseDir, "Config", "Models"),
                Path.Combine(baseDir, "Logs"),
                Path.Combine(baseDir, "Images"),
                Path.Combine(baseDir, "Data")
            };

            foreach (var dir in directories)
            {
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }
        }

        private void ConfigureServices()
        {
            var services = new ServiceCollection();

            // 注册服务
            services.AddSingleton<ILogService>(_ => LogService.Instance);
            services.AddSingleton<DatabaseService>();
            services.AddSingleton<ConfigService>();
            services.AddSingleton<PLCService>();
            services.AddSingleton<TestService>();

            ServiceProvider = services.BuildServiceProvider();
        }

        private void App_DispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            var logService = LogService.Instance;
            logService.LogError("UI异常", e.Exception);

            MessageBox.Show($"应用程序异常: {e.Exception.Message}", "系统错误",
                MessageBoxButton.OK, MessageBoxImage.Error);

            e.Handled = true;
        }

        private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            var logService = LogService.Instance;
            if (exception != null)
            {
                logService.LogError("系统异常", exception);
            }
            else
            {
                logService.LogError($"系统异常: {e.ExceptionObject}");
            }

            MessageBox.Show($"系统异常: {exception?.Message}", "严重错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // 清理资源
                this.DispatcherUnhandledException -= App_DispatcherUnhandledException;
                AppDomain.CurrentDomain.UnhandledException -= CurrentDomain_UnhandledException;

                var plcService = PLCService.Current;
                try
                {
                    plcService?.Dispose();
                }
                catch (Exception ex)
                {
                    LogService.Instance.LogError("退出时释放PLC资源失败", ex);
                }

                try
                {
                    ServiceProvider?.Dispose();
                }
                catch (Exception ex)
                {
                    LogService.Instance.LogError("释放依赖注入容器失败", ex);
                }

                if (_instanceMutex != null)
                {
                    if (_ownsMutex)
                    {
                        _instanceMutex.ReleaseMutex();
                    }
                    _instanceMutex.Dispose();
                    _instanceMutex = null;
                }
            }
            finally
            {
                base.OnExit(e);

                // 确保彻底退出进程，避免残留后台实例导致单例检测失效
                ForceTerminateProcess();
            }
        }

        private static void ForceTerminateProcess()
        {
            try
            {
                using var currentProcess = Process.GetCurrentProcess();
                if (!currentProcess.HasExited)
                {
                    currentProcess.Kill(true);
                }
            }
            catch (InvalidOperationException)
            {
                // 进程已退出或不支持终止
            }
            catch (Exception ex)
            {
                LogService.Instance.LogError("强制终止进程失败", ex);
            }
        }

    }
}
