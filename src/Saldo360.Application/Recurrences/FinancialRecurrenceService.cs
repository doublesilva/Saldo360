using Saldo360.Application.Workspaces;
using Saldo360.Application.Transactions;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Common;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Application.Recurrences;

public sealed class FinancialRecurrenceService(IFinancialWorkspaceRepository workspaces, IFinancialRecurrenceRepository repository, IFinancialRecurrenceMaterializationRepository materializations, IReferenceLookup references, TimeProvider time)
{
    public async Task<RecurrenceOperationResult> CreateAsync(Guid workspaceId, Guid userId, RecurrenceInput input, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId, userId, ct)) return new(RecurrenceAccess.NotFound, null, null);
        var (details, errors) = await Validate(workspaceId, input, ct);
        if (details is null) return new(RecurrenceAccess.Invalid, null, errors);
        var entity = FinancialRecurrence.Create(workspaceId, details, time.GetUtcNow());
        await repository.AddAsync(entity, ct);
        return new(RecurrenceAccess.Allowed, await repository.GetAsync(workspaceId, entity.Id, ct), null);
    }
    public async Task<RecurrenceDto?> GetAsync(Guid workspaceId, Guid id, Guid userId, CancellationToken ct) => await IsParticipant(workspaceId,userId,ct) ? await repository.GetAsync(workspaceId,id,ct) : null;
    public async Task<RecurrenceListResult> ListAsync(Guid workspaceId, Guid userId, bool includeArchived, CancellationToken ct) =>
        !await IsParticipant(workspaceId,userId,ct) ? new(null,null,true) : new(await repository.ListAsync(workspaceId,includeArchived,ct),null,false);
    public async Task<RecurrenceOperationResult> UpdateAsync(Guid workspaceId, Guid id, Guid userId, RecurrenceInput input, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId,userId,ct)) return new(RecurrenceAccess.NotFound,null,null);
        var entity = await repository.FindEntityAsync(workspaceId,id,ct); if (entity is null) return new(RecurrenceAccess.NotFound,null,null);
        var (details, errors) = await Validate(workspaceId,input,ct); if (details is null) return new(RecurrenceAccess.Invalid,null,errors);
        if (await repository.HasMaterializationsAsync(workspaceId, id, ct) && await ChangesCalendarAsync(entity, details)) return new(RecurrenceAccess.Conflict,null,null,"A definição de calendário não pode ser alterada depois da materialização de ocorrências.");
        try { entity.Update(details,time.GetUtcNow()); await repository.SaveAsync(ct); return new(RecurrenceAccess.Allowed,await repository.GetAsync(workspaceId,id,ct),null); }
        catch (InvalidOperationException e) { return new(RecurrenceAccess.Conflict,null,null,e.Message); }
    }
    public async Task<(MaterializationResultDto? Result, IDictionary<string,string[]>? Errors, bool NotFound, string? Conflict)> MaterializeAsync(Guid workspaceId, Guid id, Guid userId, DateOnly? from, DateOnly? through, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId,userId,ct)) return (null,null,true,null);
        if (from is null || through is null) return (null,new Dictionary<string,string[]> { ["period"]=["Informe from e through no formato yyyy-MM-dd."] },false,null);
        if (from > through) return (null,new Dictionary<string,string[]> { ["period"]=["from deve ser anterior ou igual a through."] },false,null);
        var entity=await repository.FindEntityAsync(workspaceId,id,ct); if(entity is null)return(null,null,true,null);
        if(entity.Status is RecurrenceStatus.Paused or RecurrenceStatus.Ended or RecurrenceStatus.Archived)return(null,null,false,"A recorrência não está ativa para novas materializações.");
        var account = await references.GetAccountStateAsync(workspaceId, entity.FinancialAccountId, ct);
        var category = await references.GetCategoryStateAsync(workspaceId, entity.FinancialCategoryId, ct);
        if (account is null || !account.IsActive) return (null,new Dictionary<string,string[]> { ["accountId"]=["A conta da recorrência está inválida ou inativa."] },false,null);
        if (category is null || !category.IsActive || category.Type != entity.Type) return (null,new Dictionary<string,string[]> { ["categoryId"]=["A categoria da recorrência está inválida, incompatível ou inativa."] },false,null);
        IReadOnlyList<RecurrenceOccurrenceDto> occurrences;
        try { occurrences=FinancialRecurrenceCalendar.CalculateOccurrences(entity,from.Value,through.Value); }
        catch(ArgumentException e){return(null,new Dictionary<string,string[]> { ["period"]=[e.Message] },false,null);}
        if(occurrences.Count>500)return(null,new Dictionary<string,string[]> { ["period"]=["O lote não pode conter mais de 500 ocorrências."] },false,null);
        try{return(await materializations.MaterializeAsync(entity,occurrences,userId,time.GetUtcNow(),ct),null,false,null);}
        catch(InvalidOperationException e){return(null,null,false,e.Message);}
    }
    public async Task<(IReadOnlyList<MaterializationQueryItem>? Items, string? Error, bool NotFound)> MaterializationsAsync(Guid workspaceId, Guid id, Guid userId, DateOnly? from, DateOnly? through, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId,userId,ct)) return (null,null,true);
        if (from is null || through is null) return (null,"Informe from e through no formato yyyy-MM-dd.",false);
        if (from>through)return(null,"from deve ser anterior ou igual a through.",false);
        var entity = await repository.FindEntityAsync(workspaceId,id,ct); if(entity is null)return(null,null,true);
        IReadOnlyList<RecurrenceOccurrenceDto> occurrences;
        try { occurrences=FinancialRecurrenceCalendar.CalculateOccurrences(entity,from.Value,through.Value); } catch(ArgumentException e) { return(null,e.Message,false); }
        var existing = (await materializations.ListAsync(workspaceId,id,from.Value,through.Value,ct)).ToDictionary(x=>x.OccurrenceNumber);
        return (occurrences.Select(o => existing.TryGetValue(o.Sequence,out var item) ? item : new MaterializationQueryItem(o.Sequence,o.Date,null,null,null,null)).ToList(),null,false);
    }
    public Task<RecurrenceOperationResult> ChangeAsync(Guid workspaceId, Guid id, Guid userId, string operation, CancellationToken ct) => ChangeCore(workspaceId,id,userId,operation,ct);
    private async Task<RecurrenceOperationResult> ChangeCore(Guid workspaceId, Guid id, Guid userId, string operation, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId,userId,ct)) return new(RecurrenceAccess.NotFound,null,null);
        var entity = await repository.FindEntityAsync(workspaceId,id,ct); if (entity is null) return new(RecurrenceAccess.NotFound,null,null);
        try { var now=time.GetUtcNow(); if(operation=="pause") entity.Pause(now); else if(operation=="resume") entity.Resume(now); else if(operation=="end") entity.End(now); else entity.Archive(now); await repository.SaveAsync(ct); return new(RecurrenceAccess.Allowed,await repository.GetAsync(workspaceId,id,ct),null); }
        catch (InvalidOperationException e) { return new(RecurrenceAccess.Conflict,null,null,e.Message); }
    }
    public async Task<(IReadOnlyList<RecurrenceOccurrenceDto>? Items, string? Error, bool NotFound)> OccurrencesAsync(Guid workspaceId, Guid id, Guid userId, DateOnly from, DateOnly through, CancellationToken ct)
    {
        if (!await IsParticipant(workspaceId,userId,ct)) return (null,null,true);
        var entity=await repository.FindEntityAsync(workspaceId,id,ct); if(entity is null) return (null,null,true);
        try { return (FinancialRecurrenceCalendar.CalculateOccurrences(entity,from,through),null,false); } catch(ArgumentException e) { return (null,e.Message,false); }
    }
    private async Task<bool> IsParticipant(Guid workspaceId, Guid userId, CancellationToken ct) => await workspaces.GetOwnerRoleAsync(workspaceId,userId,ct) is not null;
    private static Task<bool> ChangesCalendarAsync(FinancialRecurrence current, RecurrenceDetails next) => Task.FromResult(current.Frequency != next.Frequency || current.Interval != next.Interval || current.StartDate != next.StartDate || current.EndDate != next.EndDate || current.OccurrenceCount != next.OccurrenceCount || current.DayOfMonth != (next.Frequency == RecurrenceFrequency.Weekly ? next.StartDate.Day : next.DayOfMonth ?? next.StartDate.Day));
    private async Task<(RecurrenceDetails?, Dictionary<string,string[]>)> Validate(Guid workspaceId, RecurrenceInput input, CancellationToken ct)
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
        if(input.AccountId is { } account && !errors.ContainsKey("accountId")) { var s=await references.GetAccountStateAsync(workspaceId,account,ct); if(s is null) errors["accountId"]=["Conta inválida."]; else if(!s.IsActive) errors["accountId"]=["A conta está inativa. Escolha uma conta ativa."]; }
        if(input.CategoryId is { } category && !errors.ContainsKey("categoryId")) { var s=await references.GetCategoryStateAsync(workspaceId,category,ct); if(s is null) errors["categoryId"]=["Categoria inválida."]; else if(s.Type!=type) errors["categoryId"]=["A categoria não é compatível com o tipo."]; else if(!s.IsActive) errors["categoryId"]=["A categoria está inativa. Escolha uma categoria ativa."]; }
        return errors.Count==0 ? (new RecurrenceDetails(input.AccountId!.Value,input.CategoryId!.Value,type,description,input.Amount!.Value,input.IsEstimated,frequency,input.Interval!.Value,input.StartDate!.Value,input.EndDate,input.OccurrenceCount,input.DayOfMonth),errors) : (null,errors);
    }
}
