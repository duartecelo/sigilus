namespace Sigilus.Core.Domain;

public sealed record DetectedEntity(
    EntityType Type,
    string MatchedText,
    float Confidence,
    PdfRect Bounds,
    int PageIndex,
    DetectionSource Source,
    int CharStart,
    int CharLength);
