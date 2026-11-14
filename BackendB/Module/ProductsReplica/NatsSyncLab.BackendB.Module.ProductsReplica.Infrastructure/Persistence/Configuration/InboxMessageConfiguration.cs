using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Constants;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Persistence.Configuration;

internal sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable(DbConsts.InboxTable, DbConsts.Schema);

        builder.HasKey(message => message.EventId);
        builder.Property(message => message.EventId)
            .HasColumnName("event_id")
            .ValueGeneratedNever();

        builder.Property(message => message.StreamSequence)
            .HasColumnName("stream_sequence");

        builder.Property(message => message.ReceivedAt)
            .HasColumnName("received_at")
            .IsRequired();
    }
}
