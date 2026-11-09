using Saldo360.Application.Pluggy;
using Saldo360.Domain.Pluggy;

namespace Saldo360.Tests.Pluggy;

/// <summary>
/// T13.3: dia bancário de um instante da Pluggy. Regra determinística (sem descrição/valor/instituição): meia-noite UTC exata = "somente data" (dia UTC); qualquer outro instante = dia civil em
/// São Paulo; antes da vigência (10/11/2026 00:00 local) = dia UTC legado, sem reescrever o histórico. Caso real: Itaú envia 02:59:59.999Z para o dia 06/11 e o Saldo360 gravava 07/11.
/// </summary>
[Trait("Category", "CORE")]
[Trait("Area", "Pluggy")]
public sealed class PluggyBusinessDateTests
{
    private static DateTimeOffset Utc(string instant) => DateTimeOffset.Parse(instant, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);
    private static DateOnly Day(string value) => DateOnly.Parse(value);

    // ---- depois da vigência ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-12T02:59:59.999Z", "2026-11-11")]   // marcador de fim de dia do Itaú: 23:59:59.999 locais da véspera
    [InlineData("2026-11-12T03:00:00.000Z", "2026-11-12")]   // 00:00 locais: já é o dia seguinte
    [InlineData("2026-11-12T01:27:00.041Z", "2026-11-11")]   // instante real, 22:27 locais
    [InlineData("2026-11-12T05:56:52.000Z", "2026-11-12")]   // instante real, 02:56 locais
    [InlineData("2026-11-12T12:00:00.000Z", "2026-11-12")]   // meio do dia: UTC e local coincidem
    [InlineData("2026-11-12T23:59:59.999Z", "2026-11-12")]   // 20:59 locais
    [InlineData("2026-11-12T00:00:00.001Z", "2026-11-11")]   // 1 ms depois da meia-noite UTC já é instante real (21:00 locais da véspera)
    public void AfterTheEffectiveDateARealInstantIsTheCivilDayInSaoPaulo(string instant, string expected) =>
        Assert.Equal(Day(expected), PluggyFinancialMapper.BusinessDate(Utc(instant)));

    [Theory]
    [InlineData("2026-11-12T00:00:00.000Z", "2026-11-12")]   // contrato "somente data": o dia UTC já é o dia bancário (não a véspera)
    [InlineData("2027-01-01T00:00:00.000Z", "2027-01-01")]
    public void UtcMidnightIsTheDateOnlyContractAndKeepsTheUtcDay(string instant, string expected) =>
        Assert.Equal(Day(expected), PluggyFinancialMapper.BusinessDate(Utc(instant)));

    [Theory]
    [InlineData("2027-01-01T01:30:00Z", "2026-12-31")]   // virada de ano
    [InlineData("2026-12-01T00:30:00Z", "2026-11-30")]   // virada de mês
    [InlineData("2028-03-01T01:00:00Z", "2028-02-29")]   // ano bissexto
    [InlineData("2027-03-01T02:59:59.999Z", "2027-02-28")]   // fevereiro de ano comum
    public void TheDayTurnsBackAcrossMonthYearAndLeapDayBoundaries(string instant, string expected) =>
        Assert.Equal(Day(expected), PluggyFinancialMapper.BusinessDate(Utc(instant)));

    // ---- vigência: o histórico não é reescrito -------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-07T02:59:59.999Z", "2026-11-07")]   // o Solar real: segue como foi registrado
    [InlineData("2026-09-10T01:27:00.041Z", "2026-09-10")]   // a transferência de 12.500
    [InlineData("2026-10-27T02:59:59.999Z", "2026-10-27")]
    [InlineData("2026-11-10T02:59:59.999Z", "2026-11-10")]   // 1 ms antes da vigência: dia UTC legado
    public void BeforeTheEffectiveDateTheLegacyUtcDayIsPreserved(string instant, string expected) =>
        Assert.Equal(Day(expected), PluggyFinancialMapper.BusinessDate(Utc(instant)));

    [Fact]
    public void TheEffectiveInstantItselfAlreadyUsesTheLocalDay()
    {
        Assert.Equal(PluggyFinancialMapper.LocalBusinessDateEffectiveFrom, Utc("2026-11-10T03:00:00Z"));
        Assert.Equal(Day("2026-11-10"), PluggyFinancialMapper.BusinessDate(PluggyFinancialMapper.LocalBusinessDateEffectiveFrom));
    }

    [Fact]
    public void TheOffsetOfTheReceivedValueNeverChangesTheResult()
    {
        var utc = Utc("2026-11-12T02:59:59.999Z");
        var saoPaulo = new DateTimeOffset(2026, 11, 11, 23, 59, 59, 999, TimeSpan.FromHours(-3));   // o mesmo instante com outro deslocamento

        Assert.Equal(utc, saoPaulo);
        Assert.Equal(PluggyFinancialMapper.BusinessDate(utc), PluggyFinancialMapper.BusinessDate(saoPaulo));
    }

    // ---- efeitos: decisão do movimento e pareamento ------------------------------------------------------------------------------

    private static ImportedFacts Bank(DateTimeOffset date, string type, decimal amount, string? operation = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), PluggyLinkedAccountKind.Bank, null, PluggyTransactionStatus.Posted, type, amount, "BRL", date, "Movimento", "MOVIMENTO", operation, null, null, null,
            PluggyTransactionClass.PotentialExpense, "Test");

    [Fact]
    public void TheMovementDateFollowsTheBankDayAfterTheEffectiveDateAndTheLegacyDayBefore()
    {
        var today = new DateOnly(2026, 11, 20);

        var after = PluggyFinancialMapper.Decide(Bank(Utc("2026-11-12T02:59:59.999Z"), "DEBIT", -748.31m), today);
        var before = PluggyFinancialMapper.Decide(Bank(Utc("2026-11-07T02:59:59.999Z"), "DEBIT", -748.31m), today);

        Assert.Equal(Day("2026-11-11"), after.Date);    // o dia que o banco mostra
        Assert.Equal(Day("2026-11-07"), before.Date);   // preservado
    }

    [Fact]
    public void TheTwoLegsOfATransferKeepThePairingDayEvenWhenTheyCrossLocalMidnight()
    {
        // Saída às 22:27 locais e entrada às 02:56 locais do dia seguinte (caso real de 12.500): mesmo dia UTC, dias locais diferentes.
        var outgoing = Utc("2026-11-12T01:27:00.041Z"); var incoming = Utc("2026-11-12T05:56:52.000Z");

        Assert.NotEqual(PluggyFinancialMapper.BusinessDate(outgoing), PluggyFinancialMapper.BusinessDate(incoming));   // o dia local de cada ponta difere
        Assert.Equal(PluggyFinancialMapper.PairingDay(outgoing), PluggyFinancialMapper.PairingDay(incoming));         // a chave de pareamento (tolerância 0) continua igual

        var debits = new[] { new PairItem(Guid.NewGuid(), Guid.NewGuid(), 12500m, PluggyFinancialMapper.PairingDay(outgoing), false) };
        var credits = new[] { new PairItem(Guid.NewGuid(), Guid.NewGuid(), 12500m, PluggyFinancialMapper.PairingDay(incoming), false) };
        Assert.Single(PluggyPairing.Match(debits, credits, 0, differentLinks: true, (_, _) => false).Pairs);

        // Contraprova: pareando pelo dia local a transferência NÃO casaria e viraria despesa e receita independentes.
        var localDebits = new[] { debits[0] with { Date = PluggyFinancialMapper.BusinessDate(outgoing) } };
        var localCredits = new[] { credits[0] with { Date = PluggyFinancialMapper.BusinessDate(incoming) } };
        Assert.Empty(PluggyPairing.Match(localDebits, localCredits, 0, differentLinks: true, (_, _) => false).Pairs);
    }
}
