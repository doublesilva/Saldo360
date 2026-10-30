using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Categories;
using Saldo360.Domain.Workspaces;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialCategoryConfiguration : IEntityTypeConfiguration<FinancialCategory>
{
    /// <summary>
    /// Colação ICU não determinística que compara sem diferenciar maiúsculas de minúsculas (nível 2: acentos
    /// continuam distintos). "Mercado", "MERCADO" e "mercado" são iguais; "Saúde" e "Saude", não.
    /// </summary>
    public const string CaseInsensitiveCollation = "case_insensitive";

    /// <summary>Nome único da categoria PRINCIPAL por orçamento e tipo (subcategorias ficam fora deste índice).</summary>
    public const string UniqueNameIndex = "ux_financial_categories_workspace_id_type_name";

    /// <summary>Nome único da SUBCATEGORIA dentro da sua categoria principal.</summary>
    public const string UniqueSubcategoryNameIndex = "ux_financial_categories_parent_name";

    public const string ParentTypeForeignKey = "fk_financial_categories_parent_same_workspace_type";

    public static void ConfigureCollation(ModelBuilder modelBuilder) =>
        modelBuilder.HasCollation(CaseInsensitiveCollation, locale: "und-u-ks-level2", provider: "icu", deterministic: false);

    public void Configure(EntityTypeBuilder<FinancialCategory> builder)
    {
        builder.ToTable("financial_categories", table =>
        {
            table.HasCheckConstraint("ck_financial_categories_type",
                $"type IN ({string.Join(", ", CategoryTypes.Names.Select(name => $"'{name}'"))})");
            table.HasCheckConstraint("ck_financial_categories_name_not_blank", "btrim(name) <> ''");
            // Sem espaços nas pontas (comparação por bytes, imune à colação).
            table.HasCheckConstraint("ck_financial_categories_name_trimmed", "octet_length(name) = octet_length(btrim(name))");
            table.HasCheckConstraint("ck_financial_categories_not_own_parent", "parent_category_id IS NULL OR parent_category_id <> id");
        });

        builder.HasKey(category => category.Id);
        builder.Property(category => category.Id).ValueGeneratedNever();
        builder.Property(category => category.Name)
            .HasMaxLength(FinancialCategory.NameMaxLength)
            .IsRequired()
            .UseCollation(CaseInsensitiveCollation);
        builder.Property(category => category.Type).HasConversion<string>().HasMaxLength(20);

        // Unicidade das categorias PRINCIPAIS por orçamento e tipo, contando inativas; a colação da coluna torna o índice case-insensitive.
        builder.HasIndex(category => new { category.WorkspaceId, category.Type, category.Name })
            .IsUnique()
            .HasFilter("parent_category_id IS NULL")
            .HasDatabaseName(UniqueNameIndex);

        // Subcategoria: nome único dentro da principal (a mesma "Água" pode existir sob categorias diferentes).
        builder.HasIndex(category => new { category.ParentCategoryId, category.Name })
            .IsUnique()
            .HasFilter("parent_category_id IS NOT NULL")
            .HasDatabaseName(UniqueSubcategoryNameIndex);

        // (workspace_id, parent_category_id, type) → (workspace_id, id, type): a principal é do MESMO orçamento e do MESMO tipo. Que ela própria não
        // seja subcategoria (no máximo dois níveis) e que quem tem filhas não vire filha é garantido por trigger (migração R02).
        builder.HasOne<FinancialCategory>()
            .WithMany()
            .HasForeignKey(category => new { category.WorkspaceId, category.ParentCategoryId, category.Type })
            .HasPrincipalKey(category => new { category.WorkspaceId, category.Id, category.Type })
            .HasConstraintName(ParentTypeForeignKey)
            .OnDelete(DeleteBehavior.Restrict);

        // Restrict: categorias nunca são apagadas por cascata (não há exclusão física).
        builder.HasOne<FinancialWorkspace>()
            .WithMany()
            .HasForeignKey(category => category.WorkspaceId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(category => category.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
