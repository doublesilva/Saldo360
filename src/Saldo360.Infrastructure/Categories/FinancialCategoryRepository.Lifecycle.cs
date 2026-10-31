using Microsoft.EntityFrameworkCore;
using Npgsql;
using Saldo360.Application.Categories;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Recurrences;

namespace Saldo360.Infrastructure.Categories;

/// <summary>Impacto, exclusão e catálogo padrão: partes do ciclo de vida da categoria que dependem de olhar o orçamento inteiro.</summary>
internal sealed partial class FinancialCategoryRepository
{
    public async Task<CategoryImpactDto?> GetImpactAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken)
    {
        var category = await db.FinancialCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == categoryId)
            .Select(c => new { c.Name, c.ParentCategoryId })
            .SingleOrDefaultAsync(cancellationToken);
        if (category is null)
        {
            return null;
        }

        var subcategoryIds = await db.FinancialCategories.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && c.ParentCategoryId == categoryId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        var ids = subcategoryIds.Append(categoryId).ToList();

        // Recorrências ativas ou pausadas ainda gerariam registros na categoria: precisam ser resolvidas antes de inativar.
        var dependencies = await db.FinancialRecurrences.AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && ids.Contains(r.FinancialCategoryId)
                && (r.Status == RecurrenceStatus.Active || r.Status == RecurrenceStatus.Paused))
            .OrderBy(r => r.Description)
            .Select(r => new CategoryDependencyDto("Recurrence", r.Id, r.Description))
            .ToListAsync(cancellationToken);

        return new CategoryImpactDto(
            categoryId, category.Name, category.ParentCategoryId is not null, subcategoryIds.Count,
            await ReferencesAsync(workspaceId, ids, cancellationToken), dependencies);
    }

    public async Task<bool> TryDeleteAsync(Guid workspaceId, Guid categoryId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockWorkspaceAsync(workspaceId, cancellationToken);
            var doomed = await db.FinancialCategories
                .Where(c => c.WorkspaceId == workspaceId && (c.Id == categoryId || c.ParentCategoryId == categoryId))
                .ToListAsync(cancellationToken);
            if (doomed.All(c => c.Id != categoryId))
            {
                return false;
            }

            var references = await ReferencesAsync(workspaceId, doomed.Select(c => c.Id).ToList(), cancellationToken);
            if (references.Count > 0)
            {
                return false;
            }

            // Subcategorias primeiro: a principal é referenciada por elas (Restrict).
            db.FinancialCategories.RemoveRange(doomed.Where(c => c.Id != categoryId));
            db.FinancialCategories.Remove(doomed.Single(c => c.Id == categoryId));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // Um registro passou a usar a categoria entre a verificação e o DELETE: a integridade do banco venceu e nada foi apagado.
            return false;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    public async Task<DefaultCategoriesPlan> ApplyDefaultCategoriesAsync(
        Guid workspaceId, Guid userId, IDefaultCategoryCatalog catalog, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await LockWorkspaceAsync(workspaceId, cancellationToken);
            var existing = await db.FinancialCategories.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToListAsync(cancellationToken);
            var plan = DefaultCategoryPlanner.Plan(
                catalog, existing.Select(c => new FinancialCategoryDto(c.Id, c.Name, c.Type, c.IsActive, c.CreatedAt, c.ParentCategoryId)).ToList());
            if (plan.HasChanges)
            {
                db.FinancialCategories.AddRange(plan.CreateEntities(workspaceId, userId, now, existing.ToDictionary(c => c.Id)));
                await db.SaveChangesAsync(cancellationToken);
            }

            // Registra a inicialização na mesma transação, se ainda não houver (uma aplicação posterior não a altera). Por último de propósito: a
            // trava de escrita do banco compara a versão (xmin) do orçamento com a capturada na abertura da conexão, então nenhuma escrita
            // financeira pode vir depois desta atualização dentro da transação.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE financial_workspaces SET default_categories_initialized_at = {now}, default_categories_catalog_version = {catalog.Version}
                WHERE id = {workspaceId} AND default_categories_initialized_at IS NULL
                """, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return plan;
        }
        finally
        {
            db.ChangeTracker.Clear();
        }
    }

    /// <summary>Serializa as operações de ciclo de vida do mesmo orçamento (e as demais escritas financeiras, que travam a mesma linha).</summary>
    private Task LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM financial_workspaces WHERE id = {workspaceId} FOR UPDATE", cancellationToken);

    /// <summary>
    /// Registros que usam alguma das categorias, por origem. Inclui o histórico (cancelados, encerrados, arquivados): o vínculo existe enquanto a
    /// linha existir. Parcelas de dívida não têm chave estrangeira para a categoria; por isso a verificação é feita aqui, e não só pelo banco.
    /// </summary>
    private async Task<List<CategoryReferenceDto>> ReferencesAsync(Guid w, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        var references = new List<CategoryReferenceDto>();

        async Task Count<T>(string source, string label, IQueryable<T> query)
        {
            var count = await query.CountAsync(ct);
            if (count > 0)
            {
                references.Add(new CategoryReferenceDto(source, label, count));
            }
        }

        await Count("transactions", "Lançamentos", db.FinancialTransactions.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("plannedTransactions", "Previsões", db.FinancialPlannedTransactions.Where(x => x.WorkspaceId == w && ids.Contains(x.CategoryId)));
        await Count("recurrences", "Recorrências", db.FinancialRecurrences.Where(x => x.WorkspaceId == w && ids.Contains(x.FinancialCategoryId)));
        await Count("cardPurchases", "Compras no cartão", db.FinancialCreditCardPurchases.Where(x => x.WorkspaceId == w && ids.Contains(x.CategoryId)));
        await Count("cardInstallments", "Parcelas de fatura oficial", db.FinancialCreditCardInstallments.Where(x => x.WorkspaceId == w && x.OfficialCategoryId != null && ids.Contains(x.OfficialCategoryId.Value)));
        await Count("cardImportedInstallments", "Parcelas importadas do cartão", db.FinancialCreditCardImportedInstallments.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("cardInvoiceAdjustments", "Ajustes de fatura", db.FinancialCreditCardInvoiceAdjustments.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("debtInstallments", "Parcelas de dívidas", db.FinancialDebtInstallments.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("planLines", "Linhas do planejamento mensal", db.FinancialPlanLines.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("budgetTargets", "Limites do orçamento", db.FinancialBudgetTargets.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        await Count("pluggyPostings", "Lançamentos importados (classificação)", db.PluggyPostings.Where(x => x.WorkspaceId == w && x.CategoryId != null && ids.Contains(x.CategoryId.Value)));
        return references;
    }
}
