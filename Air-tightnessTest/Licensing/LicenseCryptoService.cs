using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LumbarMassageTest.Licensing;

public static class LicenseCryptoService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, JsonOptions);

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, JsonOptions);

    public static string BuildFingerprintHash(IEnumerable<string> parts)
    {
        var joined = string.Join("|", parts.Select(p => p.Trim().ToUpperInvariant()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    public static string SignLicense(LicenseFile license, string privateKeyPem)
    {
        var canonicalBytes = GetCanonicalPayloadBytes(license);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var signature = ecdsa.SignData(canonicalBytes, HashAlgorithmName.SHA256);

        license.Signature = new SignaturePayload
        {
            Alg = "ECDSA_P256_SHA256",
            ValueBase64 = Convert.ToBase64String(signature)
        };

        return Serialize(license);
    }

    public static bool VerifyLicense(LicenseFile license, string publicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(license.Signature.ValueBase64))
            return false;

        var canonicalBytes = GetCanonicalPayloadBytes(license);
        var signatureBytes = Convert.FromBase64String(license.Signature.ValueBase64);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(publicKeyPem);
        return ecdsa.VerifyData(canonicalBytes, signatureBytes, HashAlgorithmName.SHA256);
    }

    public static (string privateKeyPem, string publicKeyPem) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportECPrivateKeyPem(), ecdsa.ExportSubjectPublicKeyInfoPem());
    }

    private static byte[] GetCanonicalPayloadBytes(LicenseFile license)
    {
        var node = JsonSerializer.SerializeToNode(license, JsonOptions) as JsonObject
            ?? throw new InvalidOperationException("无法序列化许可证。");

        node.Remove("signature");
        var normalized = Canonicalize(node);
        return Encoding.UTF8.GetBytes(normalized.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = false
        }));
    }

    private static JsonNode Canonicalize(JsonNode node)
    {
        return node switch
        {
            JsonObject obj => CanonicalizeObject(obj),
            JsonArray arr => CanonicalizeArray(arr),
            _ => node.DeepClone()
        };
    }

    private static JsonObject CanonicalizeObject(JsonObject obj)
    {
        var normalized = new JsonObject();
        foreach (var kv in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (kv.Value is null)
                continue;

            normalized[kv.Key] = Canonicalize(kv.Value);
        }

        return normalized;
    }

    private static JsonArray CanonicalizeArray(JsonArray arr)
    {
        var normalized = new JsonArray();
        foreach (var item in arr)
        {
            if (item is null)
                continue;

            normalized.Add(Canonicalize(item));
        }

        return normalized;
    }
}
