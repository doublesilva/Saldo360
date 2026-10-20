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

    public const string UniqueNameIndex = "ux_financial_categories_workspace_id_type_name";

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
        });

        builder.HasKey(category => category.Id);
        builder.Property(category => category.Id).ValueGeneratedNever();
        builder.Property(category => category.Name)
            .HasMaxLength(FinancialCategory.NameMaxLength)
            .IsRequired()
            .UseCollation(CaseInsensitiveCollation);
        builder.Property(category => category.Type).HasConversion<string>().HasMaxLength(20);

        // Unicidade por família e tipo, contando inativas; a colação da coluna torna o índice case-insensitive.
        builder.HasIndex(category => new { category.WorkspaceId, category.Type, category.Name })
            .IsUnique()
            .HasDatabaseName(UniqueNameIndex);

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
