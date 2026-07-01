using System;
using System.IO;
using System.Text;
using LumbarMassageTest.Licensing;

namespace LumbarMassageTest.Services
{
    public class LicenseService
    {
        private const string ProductCode = "LumbarMassageTest";

        private readonly ILogService _logService;
        private readonly string _requestPath;
        private readonly string _licensePath;
        private readonly string _publicKeyPem;
        private readonly string _publicKeyPath;

        public LicenseService(ILogService logService)
        {
            _logService = logService;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;
            _requestPath = Path.Combine(baseDir, "Data", "request.dat");
            _licensePath = Path.Combine(baseDir, "Data", "license.lic");
            (_publicKeyPem, _publicKeyPath) = LoadPublicKey(baseDir);
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
                    reason = $"未找到公钥文件或公钥为空：{_publicKeyPath}";
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

        private static (string publicKeyPem, string keyPath) LoadPublicKey(string baseDir)
        {
            var keyPath = Path.Combine(baseDir, "Licensing", "Keys", "public-key.pem");
            if (!File.Exists(keyPath))
            {
                return (string.Empty, keyPath);
            }

            return (File.ReadAllText(keyPath, Encoding.UTF8), keyPath);
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
