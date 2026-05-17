using System.Runtime.CompilerServices;
using Sigilus.Core.Abstractions;
using Sigilus.Core.Domain;
using Sigilus.Detection.Validation;

namespace Sigilus.Detection;

/// <summary>
/// Decorator que descarta detecções correspondentes a entidades públicas,
/// geográficas ou genéricas (estados, cidades, órgãos públicos) — que
/// <b>não</b> devem ser pseudonimizadas. Aplica-se a entidades de tipo
/// <c>PersonName</c>, <c>Address</c> ou <c>Other</c>; tipos como CPF/CNPJ
/// passam intactos.
/// </summary>
public sealed class PublicEntityFilterDetector : IEntityDetector
{
    private readonly IEntityDetector _inner;

    public PublicEntityFilterDetector(IEntityDetector inner) => _inner = inner;

    public async IAsyncEnumerable<DetectedEntity> DetectAsync(
        PageContext page,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var ent in _inner.DetectAsync(page, ct).WithCancellation(ct))
        {
            // Tipos estruturados (CPF, CNPJ, email, telefone) sempre passam:
            // não fazem sentido como "entidade pública".
            if (ent.Type is EntityType.Cpf or EntityType.Cnpj or EntityType.Email
                       or EntityType.Phone or EntityType.Rg or EntityType.Oab
                       or EntityType.ProcessoCnj or EntityType.BankAccount)
            {
                yield return ent;
                continue;
            }

            // Heurísticas de descarte:
            // - texto muito curto (< 3 chars sem espaço, < 4 com espaço): provavelmente lixo
            //   de tokenização ou abreviação genérica;
            // - termo público conhecido (cidades, estados, órgãos);
            // - alucinação do LLM (datas, status burocrático, cargos sozinhos,
            //   números de processo, leis, OAB sem prefixo).
            var trimmed = ent.MatchedText?.Trim() ?? string.Empty;
            if (trimmed.Length < 3) continue;
            if (trimmed.Length < 5 && !trimmed.Contains(' ')) continue;

            if (PublicEntityFilter.IsPublic(trimmed)) continue;
            if (LlmHallucinationFilter.IsLikelyHallucination(ent.Type, trimmed)) continue;

            yield return ent;
        }
    }
}
