using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Planning;
using Saldo360.Domain.Workspaces;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FinancialAmountConfirmationEventConfiguration : IEntityTypeConfiguration<FinancialAmountConfirmationEvent>
{
    public void Configure(EntityTypeBuilder<FinancialAmountConfirmationEvent> b)
    {
        b.ToTable("financial_amount_confirmation_events");
        b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Competence).HasColumnType("date");
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Source).HasConversion<string>().HasMaxLength(16);
        b.Property(x => x.PreviousAmount).HasColumnType("numeric");
        b.Property(x => x.NewAmount).HasColumnType("numeric");
        b.Property(x => x.Reason).HasMaxLength(FinancialAmountConfirmationEvent.ReasonMaxLength).IsRequired();
        b.Property(x => x.Details).HasColumnType("text");
        // Consulta por competência e dedupe do diagnóstico de ambiguidade por débito.
        b.HasIndex(x => new { x.WorkspaceId, x.RecurrenceId, x.Competence, x.CreatedAt });
        b.HasIndex(x => new { x.WorkspaceId, x.SourceTransactionId, x.Kind });
        b.HasOne<FinancialWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
