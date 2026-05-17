namespace Sigilus.Core.Domain;

public readonly record struct NerSpan(
    EntityType Type,
    int CharStart,
    int CharLength,
    float Score);
