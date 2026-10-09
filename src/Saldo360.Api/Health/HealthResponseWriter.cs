using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Saldo360.Api.Health;

/// <summary>
/// Serializa o resultado dos health checks expondo apenas nome, status e duração.
/// Descrições e exceções ficam de fora para não vazar detalhes de infraestrutura.
/// </summary>
public static class HealthResponseWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var response = new HealthResponse(
            report.Status.ToString(),
            report.TotalDuration.TotalMilliseconds,
            report.Entries
                .Select(entry => new HealthCheckResponse(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Duration.TotalMilliseconds))
                .ToList());

        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsJsonAsync(response, JsonOptions);
    }
}

public sealed record HealthResponse(string Status, double TotalDurationMs, IReadOnlyList<HealthCheckResponse> Checks);

public sealed record HealthCheckResponse(string Name, string Status, double DurationMs);
