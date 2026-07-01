using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace LumbarMassageTest.Licensing;

public sealed class ActivationRequest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("product_code")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonPropertyName("product_version")]
    public string ProductVersion { get; set; } = string.Empty;

    [JsonPropertyName("edition")]
    public string Edition { get; set; } = "standard";

    [JsonPropertyName("customer_hint")]
    public string CustomerHint { get; set; } = string.Empty;

    [JsonPropertyName("fingerprint")]
    public FingerprintPayload Fingerprint { get; set; } = new();

    [JsonPropertyName("request_time_utc")]
    public DateTime RequestTimeUtc { get; set; }

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = string.Empty;
}

public sealed class FingerprintPayload
{
    [JsonPropertyName("algo")]
    public string Algo { get; set; } = "sha256";

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

public sealed class LicenseFile
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; set; } = "1.0";

    [JsonPropertyName("license_id")]
    public string LicenseId { get; set; } = string.Empty;

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "k2026_01";

    [JsonPropertyName("customer_id")]
    public string CustomerId { get; set; } = string.Empty;

    [JsonPropertyName("customer_name")]
    public string CustomerName { get; set; } = string.Empty;

    [JsonPropertyName("product_code")]
    public string ProductCode { get; set; } = string.Empty;

    [JsonPropertyName("edition")]
    public string Edition { get; set; } = "standard";

    [JsonPropertyName("features")]
    public List<string> Features { get; set; } = new();

    [JsonPropertyName("binding")]
    public LicenseBinding Binding { get; set; } = new();

    [JsonPropertyName("validity")]
    public LicenseValidity Validity { get; set; } = new();

    [JsonPropertyName("issued_at_utc")]
    public DateTime IssuedAtUtc { get; set; }

    [JsonPropertyName("issuer")]
    public string Issuer { get; set; } = string.Empty;

    [JsonPropertyName("signature")]
    public SignaturePayload Signature { get; set; } = new();
}

public sealed class LicenseBinding
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "device_fingerprint";

    [JsonPropertyName("match_threshold")]
    public int MatchThreshold { get; set; } = 100;

    [JsonPropertyName("fingerprint_algo")]
    public string FingerprintAlgo { get; set; } = "sha256";

    [JsonPropertyName("fingerprint_value")]
    public string FingerprintValue { get; set; } = string.Empty;
}

public sealed class LicenseValidity
{
    [JsonPropertyName("valid_from_utc")]
    public DateTime ValidFromUtc { get; set; }

    [JsonPropertyName("valid_to_utc")]
    public DateTime ValidToUtc { get; set; }

    [JsonPropertyName("grace_days")]
    public int GraceDays { get; set; }
}

public sealed class SignaturePayload
{
    [JsonPropertyName("alg")]
    public string Alg { get; set; } = "ECDSA_P256_SHA256";

    [JsonPropertyName("value_b64")]
    public string ValueBase64 { get; set; } = string.Empty;
}

public enum LicenseState
{
    Valid,
    GracePeriod,
    Expired,
    NotActivated,
    Tampered,
    DeviceMismatch,
    ProductMismatch,
    NotYetValid
}

public sealed class LicenseValidationResult
{
    public LicenseState State { get; set; }
    public string Message { get; set; } = string.Empty;
}
