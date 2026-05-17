using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sigilus.Core.Domain;

namespace Sigilus.Core.Audit;

public sealed record AuditLog(
    string InputSha256,
    DateTimeOffset Timestamp,
    int TotalPages,
    int TotalDecisions,
    int ApprovedDecisions,
    IReadOnlyList<AuditEntry> Entries);

public sealed record AuditEntry(
    int PageIndex,
    EntityType? Type,
    string? MatchedText,
    float? Confidence,
    DetectionSource? Source,
    PdfRect Bounds,
    bool Approved,
    string? Reason);

public static class AuditWriter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AuditLog Build(
        Stream input,
        int totalPages,
        IReadOnlyList<RedactionDecision> decisions)
    {
        if (input.CanSeek) input.Position = 0;
        var sha = ComputeSha256(input);

        var entries = decisions.Select(d => new AuditEntry(
            PageIndex: d.PageIndex,
            Type: d.Origin?.Type,
            MatchedText: d.Origin?.MatchedText,
            Confidence: d.Origin?.Confidence,
            Source: d.Origin?.Source ?? (d.Approved ? DetectionSource.Manual : null),
            Bounds: d.Bounds,
            Approved: d.Approved,
            Reason: d.Reason)).ToList();

        return new AuditLog(
            InputSha256: sha,
            Timestamp: DateTimeOffset.UtcNow,
            TotalPages: totalPages,
            TotalDecisions: decisions.Count,
            ApprovedDecisions: decisions.Count(d => d.Approved),
            Entries: entries);
    }

    public static void WriteTo(AuditLog log, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(log, Json));

    private static string ComputeSha256(Stream s)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(s);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
