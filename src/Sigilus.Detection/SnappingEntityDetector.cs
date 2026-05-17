using System.Runtime.CompilerServices;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Coordinates;

namespace Sigilus.Detection;

/// <summary>
/// Decorator que aplica <see cref="WordSnapper"/> a cada
/// <see cref="DetectedEntity"/> produzida pelo detector interno. Garante
/// que o retângulo cobre a "palavra inteira" do PDF — crítico em OCR,
/// onde o regex casa só uma parte de uma sequência fragmentada.
///
/// <para><b>Não</b> aplica snap em tipos estruturados (CPF, CNPJ, e-mail,
/// telefone, RG, OAB, processo CNJ, conta bancária) porque o regex já
/// produz o span exato — qualquer expansão grudaria prefixos como
/// "CNPJ:", "Tel.", "E-mail:" na detecção.</para>
/// </summary>
public sealed class SnappingEntityDetector : IEntityDetector
{
    private readonly IEntityDetector _inner;

    public SnappingEntityDetector(IEntityDetector inner) => _inner = inner;

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var snapper = new WordSnapper(page);
        await foreach (var ent in _inner.DetectAsync(page, ct).WithCancellation(ct))
        {
            // Tipos estruturados: regex já produz span exato — não mexe.
            if (IsStructured(ent.Type))
            {
                yield return ent;
                continue;
            }
            var snapped = snapper.Snap(ent.Bounds);
            yield return snapped.IsEmpty || snapped.Equals(ent.Bounds)
                ? ent
                : ent with { Bounds = snapped };
        }
    }

    private static bool IsStructured(EntityType t) => t is
        EntityType.Cpf or EntityType.Cnpj or EntityType.Email or
        EntityType.Phone or EntityType.Rg or EntityType.Oab or
        EntityType.ProcessoCnj or EntityType.BankAccount;
}
