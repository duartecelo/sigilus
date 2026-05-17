using Sigilus.Detection.Validation;
using Xunit;

namespace Sigilus.Detection.Tests;

public class ValidatorTests
{
    [Theory]
    [InlineData("390.533.447-05", true)]
    [InlineData("111.111.111-11", false)]
    [InlineData("123.456.789-00", false)]
    public void Cpf_validator_matches_checksum(string cpf, bool expected)
        => Assert.Equal(expected, BrazilianIdValidators.IsValidCpf(cpf));

    [Theory]
    [InlineData("11.222.333/0001-81", true)]
    [InlineData("00.000.000/0000-00", false)]
    [InlineData("12.345.678/0001-99", false)]
    public void Cnpj_validator_matches_checksum(string cnpj, bool expected)
        => Assert.Equal(expected, BrazilianIdValidators.IsValidCnpj(cnpj));
}
