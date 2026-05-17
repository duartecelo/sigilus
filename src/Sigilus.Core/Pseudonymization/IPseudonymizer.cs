using Sigilus.Core.Domain;

namespace Sigilus.Core.Pseudonymization;

/// <summary>
/// Gera um substituto determinístico para um valor sensível, mantendo
/// consistência dentro do mesmo documento (mesmo input → mesmo output).
/// </summary>
public interface IPseudonymizer
{
    /// <summary>
    /// Devolve a string que substituirá <paramref name="original"/> no PDF
    /// redigido. Implementações devem ser puras — qualquer estado de
    /// consistência mora em <see cref="PseudonymContext"/>.
    /// </summary>
    string Substitute(EntityType type, string original, PseudonymContext ctx);
}
