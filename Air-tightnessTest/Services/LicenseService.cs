using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LumbarMassageTest.Licensing;

namespace LumbarMassageTest.Services
{
    public class LicenseService
    {
        private const string ProductCode = "LumbarMassageTest";
        private const string LegacyAppDataFolderName = "LumbarMassageTest";
        private static string AppDataFolderName => typeof(LicenseService).Assembly.GetName().Name ?? "Air-tightnessTest";

        private readonly ILogService _logService;
        private readonly string _requestPath;
        private readonly string _licensePath;
        private readonly string _publicKeyPem;

        public LicenseService(ILogService logService)
        {
            _logService = logService;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var licensingRoot = ResolveLicensingRoot();
            _requestPath = Path.Combine(licensingRoot, "request.dat");
            _licensePath = Path.Combine(licensingRoot, "license.lic");
            MigrateLegacyLicenseFiles(baseDir, licensingRoot);
            _publicKeyPem = LoadEmbeddedPublicKey();
        }

        public string RequestFilePath => _requestPath;
        public string LicenseFilePath => _licensePath;

        public bool IsLicenseValid(out string reason)
        {
            reason = string.Empty;

            try
            {
                if (!File.Exists(_licensePath))
                {
                    reason = "未找到授权文件，请先导入 license.lic。";
                    return false;
                }

                var json = File.ReadAllText(_licensePath, Encoding.UTF8);

                var legacyPayload = LicenseCryptoService.Deserialize<LegacyLicensePayload>(json);
                if (legacyPayload != null && !string.IsNullOrWhiteSpace(legacyPayload.MachineFingerprint))
                {
                    return ValidateLegacyLicense(legacyPayload, out reason);
                }

                var payload = LicenseCryptoService.Deserialize<LicenseFile>(json);
                if (payload == null)
                {
                    reason = "授权文件格式无效。";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(_publicKeyPem))
                {
                    reason = "未找到内嵌公钥。";
                    return false;
                }

                var validation = LicenseValidationService.Validate(
                    payload,
                    _publicKeyPem,
                    ProductCode,
                    DeviceFingerprintService.ComputeFingerprint(),
                    DateTime.UtcNow);

                reason = validation.Message;
                return validation.State == LicenseState.Valid || validation.State == LicenseState.GracePeriod;
            }
            catch (Exception ex)
            {
                _logService.LogError("校验授权失败", ex);
                reason = "授权校验失败，请检查授权文件。";
                return false;
            }
        }

        public bool ExportRequestFile(out string error)
        {
            error = string.Empty;

            try
            {
                var request = new ActivationRequest
                {
                    RequestId = Guid.NewGuid().ToString("N"),
                    ProductCode = ProductCode,
                    ProductVersion = typeof(LicenseService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    CustomerHint = Environment.UserName,
                    Fingerprint = new FingerprintPayload { Value = DeviceFingerprintService.ComputeFingerprint() },
                    RequestTimeUtc = DateTime.UtcNow,
                    Nonce = Guid.NewGuid().ToString("N")
                };

                Directory.CreateDirectory(Path.GetDirectoryName(_requestPath)!);
                File.WriteAllText(_requestPath, LicenseCryptoService.Serialize(request), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("导出请求文件失败", ex);
                error = ex.Message;
                return false;
            }
        }

        public string GetLicenseDisplayText()
        {
            try
            {
                if (!File.Exists(_licensePath))
                {
                    return "本设备未授权";
                }

                var json = File.ReadAllText(_licensePath, Encoding.UTF8);

                var legacyPayload = LicenseCryptoService.Deserialize<LegacyLicensePayload>(json);
                if (legacyPayload != null && !string.IsNullOrWhiteSpace(legacyPayload.MachineFingerprint))
                {
                    if (!IsLicenseValid(out _))
                    {
                        return "本设备未授权";
                    }

                    if (legacyPayload.ExpireAt.HasValue)
                    {
                        return $"本设备已授权至{legacyPayload.ExpireAt.Value.ToLocalTime():yyyy年MM月dd日}";
                    }

                    return "本设备已授权";
                }

                var payload = LicenseCryptoService.Deserialize<LicenseFile>(json);
                if (payload == null || !IsLicenseValid(out _))
                {
                    return "本设备未授权";
                }

                return $"本设备已授权至{payload.Validity.ValidToUtc.ToLocalTime():yyyy年MM月dd日}";
            }
            catch
            {
                return "本设备未授权";
            }
        }

        public bool ImportLicenseFile(string sourcePath, out string error)
        {
            error = string.Empty;

            try
            {
                if (!File.Exists(sourcePath))
                {
                    error = "授权文件不存在。";
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_licensePath)!);
                File.Copy(sourcePath, _licensePath, true);
                return true;
            }
            catch (Exception ex)
            {
                _logService.LogError("导入授权文件失败", ex);
                error = ex.Message;
                return false;
            }
        }

        private static string LoadEmbeddedPublicKey()
        {
            using var keyStream = typeof(LicenseService).Assembly.GetManifestResourceStream("LumbarMassageTest.Licensing.public-key.pem");
            if (keyStream is not null)
            {
                using var reader = new StreamReader(keyStream, Encoding.UTF8);
                return reader.ReadToEnd();
            }

            return string.Empty;
        }

        private static string ResolveLicensingRoot()
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                localAppData = AppDomain.CurrentDomain.BaseDirectory;
            }

            return Path.Combine(localAppData, AppDataFolderName, "Licensing");
        }

        private static void MigrateLegacyLicenseFiles(string baseDir, string targetRoot)
        {
            Directory.CreateDirectory(targetRoot);

            foreach (var legacyPath in GetLegacyLicensePaths(baseDir))
            {
                if (!File.Exists(legacyPath))
                {
                    continue;
                }

                var targetPath = Path.Combine(targetRoot, Path.GetFileName(legacyPath));
                if (!File.Exists(targetPath))
                {
                    File.Copy(legacyPath, targetPath, false);
                }
            }
        }

        private static IEnumerable<string> GetLegacyLicensePaths(string baseDir)
        {
            yield return Path.Combine(baseDir, "Data", "request.dat");
            yield return Path.Combine(baseDir, "Data", "license.lic");

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                yield return Path.Combine(localAppData, LegacyAppDataFolderName, "Licensing", "request.dat");
                yield return Path.Combine(localAppData, LegacyAppDataFolderName, "Licensing", "license.lic");
                yield return Path.Combine(localAppData, LegacyAppDataFolderName, "Data", "request.dat");
                yield return Path.Combine(localAppData, LegacyAppDataFolderName, "Data", "license.lic");
            }
        }

        private static bool ValidateLegacyLicense(LegacyLicensePayload payload, out string reason)
        {
            reason = string.Empty;
            var localMachine = DeviceFingerprintService.ComputeFingerprint();
            if (!string.Equals(payload.MachineFingerprint, localMachine, StringComparison.OrdinalIgnoreCase))
            {
                reason = "授权文件与当前设备不匹配。";
                return false;
            }

            if (payload.ExpireAt.HasValue && payload.ExpireAt.Value < DateTime.UtcNow)
            {
                reason = $"授权已过期（UTC {payload.ExpireAt:yyyy-MM-dd HH:mm:ss}）。";
                return false;
            }

            return true;
        }

        private sealed class LegacyLicensePayload
        {
            public string MachineFingerprint { get; set; } = string.Empty;
            public DateTime? ExpireAt { get; set; }
        }
    }
}
