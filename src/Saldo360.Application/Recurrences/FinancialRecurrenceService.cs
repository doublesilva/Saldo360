using Saldo360.Application.Families;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public sealed class FinancialRecurrenceService(IFamilyRepository families, IFinancialRecurrenceRepository repository, IReferenceLookup references, TimeProvider time)
{
    public async Task<RecurrenceOperationResult> CreateAsync(Guid familyId, Guid userId, RecurrenceInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId, userId, ct)) return new(RecurrenceAccess.NotFound, null, null);
        var (details, errors) = await Validate(familyId, input, ct);
        if (details is null) return new(RecurrenceAccess.Invalid, null, errors);
        var entity = FinancialRecurrence.Create(familyId, details, time.GetUtcNow());
        await repository.AddAsync(entity, ct);
        return new(RecurrenceAccess.Allowed, await repository.GetAsync(familyId, entity.Id, ct), null);
    }
    public async Task<RecurrenceDto?> GetAsync(Guid familyId, Guid id, Guid userId, CancellationToken ct) => await IsParticipant(familyId,userId,ct) ? await repository.GetAsync(familyId,id,ct) : null;
    public async Task<RecurrenceListResult> ListAsync(Guid familyId, Guid userId, bool includeArchived, CancellationToken ct) =>
        !await IsParticipant(familyId,userId,ct) ? new(null,null,true) : new(await repository.ListAsync(familyId,includeArchived,ct),null,false);
    public async Task<RecurrenceOperationResult> UpdateAsync(Guid familyId, Guid id, Guid userId, RecurrenceInput input, CancellationToken ct)
    {
        if (!await IsParticipant(familyId,userId,ct)) return new(RecurrenceAccess.NotFound,null,null);
        var entity = await repository.FindEntityAsync(familyId,id,ct); if (entity is null) return new(RecurrenceAccess.NotFound,null,null);
        var (details, errors) = await Validate(familyId,input,ct); if (details is null) return new(RecurrenceAccess.Invalid,null,errors);
        try { entity.Update(details,time.GetUtcNow()); await repository.SaveAsync(ct); return new(RecurrenceAccess.Allowed,await repository.GetAsync(familyId,id,ct),null); }
        catch (InvalidOperationException e) { return new(RecurrenceAccess.Conflict,null,null,e.Message); }
    }
    public Task<RecurrenceOperationResult> ChangeAsync(Guid familyId, Guid id, Guid userId, string operation, CancellationToken ct) => ChangeCore(familyId,id,userId,operation,ct);
    private async Task<RecurrenceOperationResult> ChangeCore(Guid familyId, Guid id, Guid userId, string operation, CancellationToken ct)
    {
        if (!await IsParticipant(familyId,userId,ct)) return new(RecurrenceAccess.NotFound,null,null);
        var entity = await repository.FindEntityAsync(familyId,id,ct); if (entity is null) return new(RecurrenceAccess.NotFound,null,null);
        try { var now=time.GetUtcNow(); if(operation=="pause") entity.Pause(now); else if(operation=="resume") entity.Resume(now); else if(operation=="end") entity.End(now); else entity.Archive(now); await repository.SaveAsync(ct); return new(RecurrenceAccess.Allowed,await repository.GetAsync(familyId,id,ct),null); }
        catch (InvalidOperationException e) { return new(RecurrenceAccess.Conflict,null,null,e.Message); }
    }
    public async Task<(IReadOnlyList<RecurrenceOccurrenceDto>? Items, string? Error, bool NotFound)> OccurrencesAsync(Guid familyId, Guid id, Guid userId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        if (!await IsParticipant(familyId,userId,ct)) return (null,null,true);
        var entity=await repository.FindEntityAsync(familyId,id,ct); if(entity is null) return (null,null,true);
        try { return (FinancialRecurrenceCalendar.CalculateOccurrences(entity,from,through),null,false); } catch(ArgumentException e) { return (null,e.Message,false); }
    }
    private async Task<bool> IsParticipant(Guid familyId, Guid userId, CancellationToken ct) => await families.GetMemberRoleAsync(familyId,userId,ct) is not null;
    private async Task<(RecurrenceDetails?, Dictionary<string,string[]>)> Validate(Guid familyId, RecurrenceInput input, CancellationToken ct)
    {
        var errors=new Dictionary<string,string[]>(); var description=input.Description?.Trim()??string.Empty;
        if(input.AccountId is null || input.AccountId==Guid.Empty) errors["accountId"]=["Informe a conta."];
        if(input.CategoryId is null || input.CategoryId==Guid.Empty) errors["categoryId"]=["Informe a categoria."];
        if(!CategoryTypes.TryParse(input.Type,out var type)) errors["type"]=["Tipo inválido. Use Income ou Expense."];
        if(description.Length==0) errors["description"]=["Informe a descrição."]; else if(description.Length>FinancialRecurrence.DescriptionMaxLength) errors["description"]=["A descrição excede o limite."];
        if(input.Amount is null || input.Amount<=0) errors["amount"]=["O valor deve ser maior que zero."]; else if(Money.Validate(input.Amount.Value,"O valor") is { } amountError) errors["amount"]=[amountError];
        if(!Enum.TryParse<RecurrenceFrequency>(input.Frequency,true,out var frequency)) errors["frequency"]=["Frequência inválida. Use Weekly, Monthly ou Yearly."];
        if(input.Interval is < 1 or > 120) errors["interval"]=["O intervalo deve estar entre 1 e 120."];
        if(input.StartDate is null) errors["startDate"]=["Informe a data inicial."];
        if(input.EndDate is { } end && input.StartDate is { } start && end < start) errors["endDate"]=["A data final não pode ser anterior à inicial."];
        if(input.OccurrenceCount is <= 0) errors["occurrenceCount"]=["A quantidade deve ser maior que zero."];
        if(input.EndDate is not null && input.OccurrenceCount is not null) errors["endDate"]=["Informe data final ou quantidade, não ambos."];
        if(input.DayOfMonth is < 1 or > 31) errors["dayOfMonth"]=["O dia deve estar entre 1 e 31."];
        if(input.AccountId is { } account && !errors.ContainsKey("accountId")) { var s=await references.GetAccountStateAsync(familyId,account,ct); if(s is null) errors["accountId"]=["Conta inválida."]; else if(!s.IsActive) errors["accountId"]=["A conta está inativa. Escolha uma conta ativa."]; }
        if(input.CategoryId is { } category && !errors.ContainsKey("categoryId")) { var s=await references.GetCategoryStateAsync(familyId,category,ct); if(s is null) errors["categoryId"]=["Categoria inválida."]; else if(s.Type!=type) errors["categoryId"]=["A categoria não é compatível com o tipo."]; else if(!s.IsActive) errors["categoryId"]=["A categoria está inativa. Escolha uma categoria ativa."]; }
        return errors.Count==0 ? (new RecurrenceDetails(input.AccountId!.Value,input.CategoryId!.Value,type,description,input.Amount!.Value,input.IsEstimated,frequency,input.Interval!.Value,input.StartDate!.Value,input.EndDate,input.OccurrenceCount,input.DayOfMonth),errors) : (null,errors);
    }
}
