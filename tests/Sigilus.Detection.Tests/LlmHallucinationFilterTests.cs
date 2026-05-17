using Sigilus.Core.Domain;
using Sigilus.Detection.Validation;
using Xunit;

namespace Sigilus.Detection.Tests;

public class LlmHallucinationFilterTests
{
    [Theory]
    // Datas
    [InlineData(EntityType.Other, "30/04/2014", true)]
    [InlineData(EntityType.PersonName, "06/05/2014", true)]
    [InlineData(EntityType.Address, "13/09/2016", true)]
    // Anos isolados
    [InlineData(EntityType.Other, "2016", true)]
    [InlineData(EntityType.Other, "1985", true)]
    // CEP
    [InlineData(EntityType.Address, "93534-010", true)]   // só números: descarta (já há regex correto)
    [InlineData(EntityType.Address, "93534010", true)]
    // Processos
    [InlineData(EntityType.Other, "1.11.0009530-3", true)]
    [InlineData(EntityType.ProcessoCnj, "0001234-56.2024.5.02.0001", false)]   // tipo estruturado passa
    // OAB sem prefixo
    [InlineData(EntityType.Other, "46302/RS", true)]
    [InlineData(EntityType.Other, "123456/SP", true)]
    // Status burocrático
    [InlineData(EntityType.Other, "AGUARDA AUDIÊNCIA", true)]
    [InlineData(EntityType.Other, "PROCESSO DISTRIBUÍDO", true)]
    [InlineData(EntityType.Other, "CONCLUSÃO AO JUIZ", true)]
    // Cargos sozinhos
    [InlineData(EntityType.PersonName, "Promotor(a) de Justiça", true)]
    [InlineData(EntityType.PersonName, "Diretor", true)]
    [InlineData(EntityType.PersonName, "Excelentíssimo", true)]
    // Leis
    [InlineData(EntityType.Other, "Lei nº 8.625/1993", true)]
    [InlineData(EntityType.Other, "Art. 129", true)]
    // URL
    [InlineData(EntityType.Other, "http://www.tjrs.jus.br", true)]
    // Casos VÁLIDOS — não devem ser descartados
    [InlineData(EntityType.PersonName, "Manoel Luiz Prates Guimarães", false)]
    [InlineData(EntityType.PersonName, "Cristiano Araújo da Silva", false)]
    [InlineData(EntityType.Address, "Rua das Flores, 200", false)]
    // CPF/CNPJ sempre passam (já validados por checksum)
    [InlineData(EntityType.Cpf, "390.533.447-05", false)]
    [InlineData(EntityType.Email, "joao@example.com", false)]
    public void Filter_corretly_identifies_hallucinations(EntityType type, string text, bool expected)
        => Assert.Equal(expected, LlmHallucinationFilter.IsLikelyHallucination(type, text));
}
