using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;
using Saldo360.Domain.Users;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FamilyMemberConfiguration : IEntityTypeConfiguration<FamilyMember>
{
    public void Configure(EntityTypeBuilder<FamilyMember> builder)
    {
        builder.ToTable("family_members", table => table.HasCheckConstraint(
            "ck_family_members_role",
            $"role IN ('{nameof(FamilyRole.Owner)}', '{nameof(FamilyRole.Member)}')"));

        builder.HasKey(member => member.Id);
        builder.Property(member => member.Id).ValueGeneratedNever();
        builder.Property(member => member.Role).HasConversion<string>().HasMaxLength(20);

        // Um usuário aparece no máximo uma vez por família; não há limite de membros.
        builder.HasIndex(member => new { member.FamilyId, member.UserId }).IsUnique();
        builder.HasIndex(member => member.UserId);

        builder.HasOne<ApplicationUser>()
            .WithMany()
            .HasForeignKey(member => member.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
