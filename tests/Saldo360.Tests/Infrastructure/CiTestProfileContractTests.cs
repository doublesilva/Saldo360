using System.Reflection;
using Saldo360.Tests.Api;
using Saldo360.Tests.Persistence;

namespace Saldo360.Tests.Infrastructure;

// Executable selection metadata is a CI safety boundary, not a test-count target.
[Trait("Category", "CORE")]
[Trait("Area", "Infrastructure")]
[Trait("Profile", "FAST")]
public sealed class CiTestProfileContractTests
{
    private static readonly HashSet<string> Areas =
    [
        "Accounts", "Admin", "Budgets", "Cards", "Categories", "Dashboard", "Debts",
        "Documents", "Families", "Goals", "Infrastructure", "Planning", "Platform",
        "Recurrences", "Scenarios", "Security", "Services", "Transactions", "Transfers", "Workspaces"
    ];

    [Fact]
    public void EveryTestHasExplicitKnownAreasAndFastHasNoNativeOcrDependency()
    {
        foreach (var type in typeof(CiTestProfileContractTests).Assembly.GetTypes())
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.GetCustomAttributes<FactAttribute>().Any()))
        {
            var traits = Traits(type, method).ToArray();
            var areas = traits.Where(t => t.Key == "Area").Select(t => t.Value).ToArray();
            Assert.True(areas.Length > 0, $"Missing Area: {type.Name}.{method.Name}");
            Assert.All(areas, area => Assert.Contains(area, Areas));
            var collection = type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CollectionAttribute));
            if (collection?.ConstructorArguments[0].Value is string name && name.StartsWith("postgres", StringComparison.Ordinal))
                Assert.Contains(new KeyValuePair<string, string>("Dependency", "PostgreSQL"), traits);
            if (traits.Contains(new("Profile", "FAST")))
                Assert.DoesNotContain(new KeyValuePair<string, string>("Dependency", "NativeOCR"), traits);
        }
    }

    [Fact]
    public void CriticalFinancialAndSecuritySentinelsRemainInFast()
    {
        (Type Type, string Method)[] sentinels =
        [
            (typeof(AuthEndpointsTests), nameof(AuthEndpointsTests.Logout_WithoutCsrfToken_IsRejected_AndSessionStaysValid)),
            (typeof(SessionIdentityTests), nameof(SessionIdentityTests.SameSubject_FromDifferentIssuers_AreDistinctIdentities)),
            (typeof(FinancialWorkspaceEndpointsTests), nameof(FinancialWorkspaceEndpointsTests.SocialMembership_DoesNotGrantPersonalFinancialAccess_InEitherDirection)),
            (typeof(FinancialTransactionEndpointsTests), nameof(FinancialTransactionEndpointsTests.IncomeAndExpense_PersistWithExactCentsAndDate)),
            (typeof(FinancialTransactionEndpointsTests), nameof(FinancialTransactionEndpointsTests.Cancel_IsIdempotent_PreservesRow_HidesByDefault_AndBlocksEdit)),
            (typeof(FinancialTransferEndpointsTests), nameof(FinancialTransferEndpointsTests.Acceptance_PJ2000_PF100_Transfer500_Gives1500And600_WithoutTouchingIncomeOrExpense)),
            (typeof(FinancialCreditCardIntegrationAuditTests), nameof(FinancialCreditCardIntegrationAuditTests.InstallmentsPaymentAndCancellationRemainConsistentAcrossFinancialModules)),
            (typeof(PlannedRealizationEndpointsTests), nameof(PlannedRealizationEndpointsTests.ConcurrentRealizations_CreateExactlyOneTransaction)),
            (typeof(FinancialBudgetExecutionPersistenceTests), nameof(FinancialBudgetExecutionPersistenceTests.ConcurrentRealizationCannotDisappearBetweenRealizedAndCommittedAggregates)),
            (typeof(AccountProjectionPersistenceTests), nameof(AccountProjectionPersistenceTests.ConsistentRead_SeesOneSnapshot_EvenIfARealizationCommitsInTheMiddle)),
            (typeof(KnownStatementImportTests), nameof(KnownStatementImportTests.FinalSaveFailureRollsBackAllRowsAndConfirmationJournal))
        ];
        foreach (var (type, name) in sentinels)
            Assert.Contains(new KeyValuePair<string, string>("Profile", "FAST"), Traits(type, type.GetMethod(name)!));
    }

    private static IEnumerable<KeyValuePair<string, string>> Traits(Type type, MethodInfo method) =>
        type.GetCustomAttributesData().Concat(method.GetCustomAttributesData())
            .Where(a => a.AttributeType == typeof(TraitAttribute))
            .Select(a => new KeyValuePair<string, string>((string)a.ConstructorArguments[0].Value!,
                (string)a.ConstructorArguments[1].Value!));
}
