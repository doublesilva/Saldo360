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
        builder.Property(session => session.Subject).HasMaxLength(255);
        builder.HasIndex(session => session.Subject);
        builder.HasIndex(session => session.ExpiresAt);
    }
}
