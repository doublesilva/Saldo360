using System.Globalization;
using Saldo360.Application.Families;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Common;
using Saldo360.Domain.Debts;
using Saldo360.Domain.Transactions;

namespace Saldo360.Application.Debts;
public sealed class FinancialDebtInstallmentService(IFamilyRepository families, IFinancialDebtInstallmentRepository repo, IReferenceLookup refs, TimeProvider time)
{
 public async Task<(IReadOnlyList<InstallmentDto>? Items,bool NotFound)> ListAsync(Guid f,Guid d,Guid u,CancellationToken ct)=>await families.GetMemberRoleAsync(f,u,ct) is null?(null,true):(await repo.GetDebtAsync(f,d,ct) is null?(null,true):(await repo.ListInstallmentsAsync(f,d,ct),false));
 public async Task<InstallmentResult> CreateAsync(Guid f,Guid d,Guid u,InstallmentInput i,CancellationToken ct){if(await families.GetMemberRoleAsync(f,u,ct) is null||await repo.GetDebtAsync(f,d,ct) is not {Status:DebtStatus.Active})return new(DebtInstallmentAccess.NotFound,null,null);var e=new Dictionary<string,string[]>();if(i.InstallmentNumber is null or <=0)e["installmentNumber"]=["Número inválido."];if(i.DueDate is null)e["dueDate"]=["Informe a data."];TryMoney(i.ScheduledAmount,out var amount,e,"scheduledAmount",true);var comps=new[]{("principalComponent",i.PrincipalComponent),("interestComponent",i.InterestComponent),("insuranceComponent",i.InsuranceComponent),("otherChargesComponent",i.OtherChargesComponent)};var vals=new decimal?[4];for(var n=0;n<4;n++)if(comps[n].Item2 is not null){if(TryMoney(comps[n].Item2,out var cv,e,comps[n].Item1,false))vals[n]=cv;}if(e.Count>0)return new(DebtInstallmentAccess.Invalid,null,e);if(vals.Any(x=>x is not null)&&vals.Where(x=>x is not null).Sum(x=>x!.Value)>amount)return new(DebtInstallmentAccess.Invalid,null,new Dictionary<string,string[]>{{"components",["Componentes excedem o valor nominal."]}});var x=FinancialDebtInstallment.Create(f,d,new(i.InstallmentNumber!.Value,i.DueDate!.Value,amount,vals[0],vals[1],vals[2],vals[3]),time.GetUtcNow());try{await repo.AddInstallmentAsync(x,ct);}catch(Exception){return new(DebtInstallmentAccess.Conflict,null,null,"Parcela já cadastrada.");}return new(DebtInstallmentAccess.Allowed,(await repo.ListInstallmentsAsync(f,d,ct)).Single(z=>z.Id==x.Id),null);}
 public async Task<IReadOnlyList<PaymentDto>?> PaymentsAsync(Guid f,Guid d,Guid i,Guid u,CancellationToken ct)=>await families.GetMemberRoleAsync(f,u,ct) is null||await repo.GetInstallmentAsync(f,d,i,ct) is null?null:await repo.ListPaymentsAsync(f,i,ct);
 public async Task<PaymentResult> PayAsync(Guid f,Guid d,Guid i,Guid u,PaymentInput input,CancellationToken ct)
 {
  if(await families.GetMemberRoleAsync(f,u,ct) is null)return new(DebtInstallmentAccess.NotFound,null,null);
  var debt=await repo.GetDebtAsync(f,d,ct);var inst=await repo.GetInstallmentAsync(f,d,i,ct);
  if(debt is null||inst is null||debt.Status!=DebtStatus.Active)return new(DebtInstallmentAccess.NotFound,null,null);
  var e=new Dictionary<string,string[]>();
  var operationId=input.OperationId?.Trim();
  if(string.IsNullOrWhiteSpace(operationId))e["operationId"]=["Informe a operação."];
  else if(operationId.Length>100)e["operationId"]=["A operação deve ter no máximo 100 caracteres."];
  if(input.FinancialAccountId is null)e["financialAccountId"]=["Informe a conta."];
  TryMoney(input.Amount,out var amount,e,"amount",true);
  var vals=new decimal?[4];var fields=new[]{("principalPaid",input.PrincipalPaid),("interestPaid",input.InterestPaid),("insurancePaid",input.InsurancePaid),("otherChargesPaid",input.OtherChargesPaid)};
  for(var n=0;n<4;n++)if(fields[n].Item2 is not null&&TryMoney(fields[n].Item2,out var pv,e,fields[n].Item1,false))vals[n]=pv;
  if(e.Count>0)return new(DebtInstallmentAccess.Invalid,null,e);
  var now=time.GetUtcNow();var paymentDate=input.PaymentDate??BusinessCalendar.Today(now);var accountId=input.FinancialAccountId!.Value;
  if(await repo.FindPaymentByOperationAsync(f,operationId!,ct) is { } existing)
   return await repo.PaymentPayloadMatchesAsync(f,operationId!,d,i,accountId,paymentDate,amount,vals[0],vals[1],vals[2],vals[3],ct)
    ?new(DebtInstallmentAccess.Allowed,await repo.GetPaymentAsync(f,existing.Id,ct),null)
    :new(DebtInstallmentAccess.Conflict,null,null,"OperationId já utilizado com conteúdo diferente.");
  var account=await refs.GetAccountStateAsync(f,accountId,ct);if(account is null||!account.IsActive)return Invalid("financialAccountId","Conta inválida ou inativa.");
  var paid=(await repo.ListPaymentsAsync(f,i,ct)).Where(x=>!x.IsCancelled).Sum(x=>x.Amount);if(paid+amount>inst.ScheduledAmount)return Invalid("amount","Pagamento excede o saldo da parcela.");
  var t=FinancialTransaction.CreateDebtInstallmentPayment(f,accountId,$"Pagamento parcela {inst.InstallmentNumber} - {debt.Name}",amount,paymentDate,u,now);
  var p=FinancialDebtPayment.Create(f,d,i,accountId,new(operationId!,paymentDate,amount,vals[0],vals[1],vals[2],vals[3]),t.Id,now);
  try{await repo.AddPaymentAsync(p,t,ct);}
  catch(Exception)
  {
   if(await repo.FindPaymentByOperationAsync(f,operationId!,ct) is { } idem)
    return await repo.PaymentPayloadMatchesAsync(f,operationId!,d,i,accountId,paymentDate,amount,vals[0],vals[1],vals[2],vals[3],ct)
     ?new(DebtInstallmentAccess.Allowed,await repo.GetPaymentAsync(f,idem.Id,ct),null)
     :new(DebtInstallmentAccess.Conflict,null,null,"OperationId já utilizado com conteúdo diferente.");
   return new(DebtInstallmentAccess.Conflict,null,null,"Operação concorrente ou inválida.");
  }
  return new(DebtInstallmentAccess.Allowed,await repo.GetPaymentAsync(f,p.Id,ct),null);
 }
 public async Task<bool> CancelAsync(Guid f,Guid d,Guid i,Guid p,Guid u,CancellationToken ct)=>await families.GetMemberRoleAsync(f,u,ct) is not null&&await repo.CancelPaymentAsync(f,p,u,time.GetUtcNow(),ct);
 private static PaymentResult Invalid(string k,string m)=>new(DebtInstallmentAccess.Invalid,null,new Dictionary<string,string[]>{{k,[m]}});
 private static bool TryMoney(string? s,out decimal v,Dictionary<string,string[]> e,string key,bool required){v=0;if(string.IsNullOrWhiteSpace(s)){if(required)e[key]=["Informe um valor."];return !required;}if(!decimal.TryParse(s,NumberStyles.Number,CultureInfo.InvariantCulture,out v)||v<=0||Money.Validate(v,"O valor") is not null){e[key]=["Valor decimal inválido."];return false;}return true;}
}
