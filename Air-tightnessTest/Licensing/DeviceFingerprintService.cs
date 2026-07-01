using System;
using System.Runtime.InteropServices;

namespace LumbarMassageTest.Licensing;

public static class DeviceFingerprintService
{
    public static string ComputeFingerprint()
    {
        string[] parts =
        {
            Environment.MachineName ?? string.Empty,
            Environment.UserDomainName ?? string.Empty,
            RuntimeInformation.OSDescription ?? string.Empty,
            RuntimeInformation.OSArchitecture.ToString(),
            Environment.ProcessorCount.ToString(),
            Environment.SystemDirectory ?? string.Empty
        };

        return LicenseCryptoService.BuildFingerprintHash(parts);
    }
}
