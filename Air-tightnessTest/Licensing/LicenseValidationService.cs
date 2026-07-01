using System;
namespace LumbarMassageTest.Licensing;

public static class LicenseValidationService
{
    public static LicenseValidationResult Validate(
        LicenseFile? license,
        string publicKeyPem,
        string expectedProductCode,
        string currentFingerprint,
        DateTime nowUtc)
    {
        if (license is null)
            return new LicenseValidationResult { State = LicenseState.NotActivated, Message = "未导入许可证。" };

        if (!string.Equals(license.ProductCode, expectedProductCode, StringComparison.OrdinalIgnoreCase))
            return new LicenseValidationResult { State = LicenseState.ProductMismatch, Message = "许可证产品不匹配。" };

        if (!LicenseCryptoService.VerifyLicense(license, publicKeyPem))
            return new LicenseValidationResult { State = LicenseState.Tampered, Message = "许可证签名无效或内容被篡改。" };

        if (!string.Equals(license.Binding.FingerprintValue, currentFingerprint, StringComparison.OrdinalIgnoreCase))
            return new LicenseValidationResult { State = LicenseState.DeviceMismatch, Message = "许可证与当前设备不匹配。" };

        if (nowUtc < license.Validity.ValidFromUtc)
            return new LicenseValidationResult { State = LicenseState.NotYetValid, Message = "许可证尚未生效。" };

        if (nowUtc <= license.Validity.ValidToUtc)
            return new LicenseValidationResult { State = LicenseState.Valid, Message = "许可证有效。" };

        if (nowUtc <= license.Validity.ValidToUtc.AddDays(license.Validity.GraceDays))
            return new LicenseValidationResult { State = LicenseState.GracePeriod, Message = "许可证已过期，当前处于宽限期。" };

        return new LicenseValidationResult { State = LicenseState.Expired, Message = "许可证已过期。" };
    }
}
