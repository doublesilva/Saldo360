using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FamilyInvitationConfiguration : IEntityTypeConfiguration<FamilyInvitation>
{
    public void Configure(EntityTypeBuilder<FamilyInvitation> builder)
    {
        builder.ToTable("family_invitations", table =>
        {
            // Só o hash do token (SHA-256) é guardado.
            table.HasCheckConstraint("ck_family_invitations_token_hash_size",
                $"octet_length(token_hash) = {FamilyInvitation.TokenHashSizeInBytes}");
            table.HasCheckConstraint("ck_family_invitations_expiration", "expires_at > created_at");
            // Um convite termina de um único jeito: consumido ou revogado, nunca os dois.
            table.HasCheckConstraint("ck_family_invitations_single_outcome",
                "consumed_at IS NULL OR revoked_at IS NULL");
            table.HasCheckConstraint("ck_family_invitations_consumed_by",
                "(consumed_at IS NULL) = (consumed_by_user_id IS NULL)");
        });

        builder.HasKey(invitation => invitation.Id);
        builder.Property(invitation => invitation.Id).ValueGeneratedNever();
        builder.Property(invitation => invitation.TokenHash).IsRequired();
        builder.HasIndex(invitation => invitation.TokenHash).IsUnique();
        builder.HasIndex(invitation => invitation.FamilyId);

        builder.HasOne<Family>()
            .WithMany()
            .HasForeignKey(invitation => invitation.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(invitation => invitation.CreatedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(invitation => invitation.ConsumedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
