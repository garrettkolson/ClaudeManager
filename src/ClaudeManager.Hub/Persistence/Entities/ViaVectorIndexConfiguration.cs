using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClaudeManager.Hub.Persistence.Entities;

public class ViaVectorIndexConfiguration : IEntityTypeConfiguration<ViaVectorIndex>
{
    public void Configure(EntityTypeBuilder<ViaVectorIndex> builder)
    {
        builder.Property(v => v.Id)
            .ValueGeneratedOnAdd();

        builder.Property(v => v.Embedding);

        builder.HasIndex(v => v.SessionId)
            .HasDatabaseName("IX_ViaVectorIndex_SessionId");
    }
}
