using System.Net;
using System.Text.Json;
using Saldo360.Domain.Debts;
using Saldo360.Tests.Support;
namespace Saldo360.Tests.Api;
[Collection(PostgresCollection.Name)]
[Trait("Category", "CORE")]
[Trait("Area", "Debts")]
[Trait("Dependency", "PostgreSQL")]
public sealed class FinancialDebtBudgetIntegrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
 [Fact]
 public async Task StoredCategorizedInstallment_IsCommittedOnceInBudget()
 {
  var user=await SignInAsync("debt-budget-owner");var family=await CreateWorkspaceAsync(user,"Família Orçamento Dívida");
  var category=await SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{family.Id}/categories",user,new{name="Financiamento",type="Expense"});using var cj=JsonDocument.Parse(await category.Content.ReadAsStringAsync());var categoryId=cj.RootElement.GetProperty("id").GetGuid();
  var budget=await SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{family.Id}/budgets",user,new{competence="2026-11",description="Novembro"});using var bj=JsonDocument.Parse(await budget.Content.ReadAsStringAsync());var budgetId=bj.RootElement.GetProperty("id").GetGuid();
  var target=await SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{family.Id}/budgets/{budgetId}/targets",user,new{name="Financiamento",groupingType="Category",categoryId,plannedAmount="1500.00"});Assert.Equal(HttpStatusCode.Created,target.StatusCode);
  var debt=await SendWithCsrfAsync(HttpMethod.Post,$"/api/financial-workspaces/{family.Id}/debts",user,new{name="Dívida",debtType="Other",originalPrincipal="1000.00",contractDate="2026-01-01"});using var dj=JsonDocument.Parse(await debt.Content.ReadAsStringAsync());var debtId=dj.RootElement.GetProperty("id").GetGuid();
  // Exercise the budget read contract with a genuinely categorized stored installment.
  // The HTTP creation service currently drops CategoryId; that separate application gap is
  // recorded in docs/ci-test-audit.md, not patched or hidden behind a zero-amount expectation.
  await using(var db=Db())
  {
   db.FinancialDebtInstallments.Add(FinancialDebtInstallment.Create(family.Id,debtId,
    new(1,new DateOnly(2026,11,10),1000m,null,null,null,null,categoryId),DateTimeOffset.UtcNow));
   await db.SaveChangesAsync();
  }
  var execution=await SendAsync(HttpMethod.Get,$"/api/financial-workspaces/{family.Id}/budgets/{budgetId}/execution",user);
  Assert.Equal(HttpStatusCode.OK,execution.StatusCode);
  using var result=JsonDocument.Parse(await execution.Content.ReadAsStringAsync());
  var amounts=Assert.Single(result.RootElement.GetProperty("targets").EnumerateArray());
  Assert.Equal("1000.00",amounts.GetProperty("committed").GetString());
  Assert.Equal("1000.00",amounts.GetProperty("projectedConsumption").GetString());
  Assert.Equal("0.00",amounts.GetProperty("realized").GetString());
  Assert.Equal("1000.00",result.RootElement.GetProperty("categories").GetProperty("committed").GetString());
 }
}
