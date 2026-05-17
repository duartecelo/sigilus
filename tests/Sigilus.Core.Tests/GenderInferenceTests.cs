using Sigilus.Core.Pseudonymization;
using Xunit;

namespace Sigilus.Core.Tests;

public class GenderInferenceTests
{
    [Theory]
    [InlineData("João da Silva", Gender.Masculine)]
    [InlineData("Maria Santos Pereira", Gender.Feminine)]
    [InlineData("Cristiano Araújo da Silva", Gender.Masculine)]   // bug: estava virando Feminine
    [InlineData("Manoel Luiz Prates Guimarães", Gender.Masculine)]
    [InlineData("Renato Vogel", Gender.Masculine)]
    [InlineData("Celso Prezzi", Gender.Masculine)]
    [InlineData("Beatriz Almeida", Gender.Feminine)]
    [InlineData("Andrea Bocelli", Gender.Masculine)]   // exceção: "Andrea" pode ser masc
    // Texto OCR com quebras de linha — primeiro token vale
    [InlineData("Cristiano\nAraújo\nda\nSilva", Gender.Masculine)]
    [InlineData("Maria\nSantos", Gender.Feminine)]
    [InlineData("Renato\nVogel", Gender.Masculine)]
    public void Infer_correctly_classifies(string fullName, Gender expected)
        => Assert.Equal(expected, GenderInference.Infer(fullName));
}
