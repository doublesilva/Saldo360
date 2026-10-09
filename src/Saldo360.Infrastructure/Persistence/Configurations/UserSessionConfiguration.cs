using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Infrastructure.Authentication;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public void Configure(EntityTypeBuilder<UserSession> builder)
    {
        builder.ToTable("user_sessions");
        builder.HasKey(session => session.Id);
        builder.Property(session => session.Id).HasMaxLength(64);
        // Nulo só em sessões anteriores ao registro do emissor (recusadas na leitura).
        builder.Property(session => session.Issuer).HasMaxLength(2048);
        builder.Property(session => session.Subject).HasMaxLength(255);
        // Não é único: a mesma identidade pode ter várias sessões (dispositivos, abas).
        builder.HasIndex(session => new { session.Issuer, session.Subject });
        builder.HasIndex(session => session.ExpiresAt);
    }
}
