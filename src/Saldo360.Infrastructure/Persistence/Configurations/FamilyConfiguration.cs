using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Saldo360.Domain.Families;

namespace Saldo360.Infrastructure.Persistence.Configurations;

internal sealed class FamilyConfiguration : IEntityTypeConfiguration<Family>
{
    public void Configure(EntityTypeBuilder<Family> builder)
    {
        builder.ToTable("families");
        builder.HasKey(family => family.Id);
        builder.Property(family => family.Id).ValueGeneratedNever();
        builder.Property(family => family.Name).HasMaxLength(Family.NameMaxLength).IsRequired();

        builder.HasMany(family => family.Members)
            .WithOne()
            .HasForeignKey(member => member.FamilyId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(family => family.Members).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
