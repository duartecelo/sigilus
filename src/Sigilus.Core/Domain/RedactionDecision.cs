namespace Sigilus.Core.Domain;

/// <summary>
/// Decisão final do usuário (ou heurística automática) sobre uma redação.
/// Bounds está em PDF user-space.
/// </summary>
public sealed record RedactionDecision(
    PdfRect Bounds,
    int PageIndex,
    bool Approved,
    string? Reason,
    DetectedEntity? Origin);
