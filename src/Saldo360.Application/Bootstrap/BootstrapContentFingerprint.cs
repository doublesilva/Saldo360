using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Saldo360.Application.Bootstrap;

/// <summary>Versioned fingerprint for strongly typed bootstrap contracts. Metadata is evidence, not discarded.</summary>
public static class BootstrapContentFingerprint
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new DecimalConverter() } };
    public static string Compute<T>(string entityType, T content) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { version = 1, entityType, content }, Options)));

    private sealed class DecimalConverter : JsonConverter<decimal>
    {
        public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDecimal();
        public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteRawValue(value.ToString("G29", CultureInfo.InvariantCulture));
    }
}
