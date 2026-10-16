using System.Net;
using System.Text.Json;
using Saldo360.Tests.Support;
namespace Saldo360.Tests.Api;
[Collection(PostgresCollection.Name)]
public sealed class FinancialDebtBudgetIntegrationTests(PostgresFixture postgres) : PostgresApiTestBase(postgres)
{
 [Fact]
 public async Task CategorizedInstallment_IsCommittedOnceInBudget()
 {
  var user=await SignInAsync("debt-budget-owner");var family=await CreateFamilyAsync(user,"Família Orçamento Dívida");
  var category=await SendWithCsrfAsync(HttpMethod.Post,$"/api/families/{family.Id}/categories",user,new{name="Financiamento",type="Expense"});using var cj=JsonDocument.Parse(await category.Content.ReadAsStringAsync());var categoryId=cj.RootElement.GetProperty("id").GetGuid();
  var budget=await SendWithCsrfAsync(HttpMethod.Post,$"/api/families/{family.Id}/budgets",user,new{competence="2026-11",description="Novembro"});using var bj=JsonDocument.Parse(await budget.Content.ReadAsStringAsync());var budgetId=bj.RootElement.GetProperty("id").GetGuid();
  var target=await SendWithCsrfAsync(HttpMethod.Post,$"/api/families/{family.Id}/budgets/{budgetId}/targets",user,new{name="Financiamento",groupingType="Category",categoryId,plannedAmount="1500.00"});Assert.Equal(HttpStatusCode.Created,target.StatusCode);
  var debt=await SendWithCsrfAsync(HttpMethod.Post,$"/api/families/{family.Id}/debts",user,new{name="Dívida",debtType="Other",originalPrincipal="1000.00",contractDate="2026-01-01"});using var dj=JsonDocument.Parse(await debt.Content.ReadAsStringAsync());var debtId=dj.RootElement.GetProperty("id").GetGuid();
  var installment=await SendWithCsrfAsync(HttpMethod.Post,$"/api/families/{family.Id}/debts/{debtId}/installments",user,new{installmentNumber=1,dueDate="2026-11-10",scheduledAmount="1000.00",categoryId});Assert.Equal(HttpStatusCode.Created,installment.StatusCode);
  var execution=await SendAsync(HttpMethod.Get,$"/api/families/{family.Id}/budgets/{budgetId}/execution",user);Assert.Equal(HttpStatusCode.OK,execution.StatusCode);var text=await execution.Content.ReadAsStringAsync();Assert.Contains("targets",text,StringComparison.OrdinalIgnoreCase);
 }
}
