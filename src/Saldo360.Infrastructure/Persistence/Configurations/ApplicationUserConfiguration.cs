using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("application_users");
        builder.HasKey(user => user.Id);
        builder.Property(user => user.Id).ValueGeneratedNever();
        builder.Property(user => user.Issuer).HasMaxLength(ApplicationUser.IssuerMaxLength).IsRequired();
        builder.Property(user => user.Subject).HasMaxLength(ApplicationUser.SubjectMaxLength).IsRequired();
        builder.Property(user => user.DisplayName).HasMaxLength(ApplicationUser.DisplayNameMaxLength);

        // Colunas varchar com a collation padrão (determinística): o índice único é sensível à caixa
        // e não normaliza nada, como exige a identidade externa.
        builder.HasIndex(user => new { user.Issuer, user.Subject }).IsUnique();
    }
}
