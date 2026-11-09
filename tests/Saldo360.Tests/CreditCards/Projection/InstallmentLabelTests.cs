using Saldo360.Application.CreditCards.Projection;

namespace Saldo360.Tests.CreditCards.Projection;

/// <summary>T13.3: um único identificador de parcela confiável; a descrição original e a numeração do banco ficam rastreáveis e uma divergência é sinalizada, nunca corrigida.</summary>
[Trait("Category", "CORE")]
[Trait("Area", "Cards")]
public sealed class InstallmentLabelTests
{
    [Fact]
    public void TheDuplicatedMarkersAndTheMerchantSuffixCollapseIntoOneReadableLabelAndTheOriginalIsPreserved()
    {
        var label = InstallmentLabel.Build("Mercadolivre*Mercado 4/4 (2/4)", null, null);

        Assert.Equal("Mercado Livre — Parcela 2/4", label.Display);
        Assert.Equal("Mercadolivre*Mercado 4/4 (2/4)", label.OriginalDescription);
        Assert.Equal((2, 4, 4, 4), (label.Number, label.Total, label.OriginalNumber, label.OriginalTotal));
        Assert.True(label.Inconsistent);   // o banco escreveu 4/4 e o motor usa 2/4: sinaliza, não renumera
    }

    [Theory]
    [InlineData("NETFLIX 02/10", 2, 10, "Netflix — Parcela 2/10", false)]
    [InlineData("Geladeira", 3, 3, "Geladeira — Parcela 3/3", false)]
    [InlineData("LOJA CENTRAL PARC 1/3", 2, 3, "Loja Central — Parcela 2/3", true)]
    public void TheStructuredNumberAndTotalAlwaysWin(string description, int number, int total, string display, bool inconsistent)
    {
        var label = InstallmentLabel.Build(description, number, total);

        Assert.Equal((display, inconsistent), (label.Display, label.Inconsistent));
    }

    [Fact]
    public void ADescriptionWithoutAnyInstallmentIsOnlyTidied()
    {
        var label = InstallmentLabel.Build("Mercado no cartão", null, null);

        Assert.Equal(("Mercado no cartão", null, false), (label.Display, label.Number, label.Inconsistent));
    }
}
