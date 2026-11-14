using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NatsSyncLab.BackendA.Module.Products.Infrastructure.Constants;
using NatsSyncLab.SharedKernel.Persistence;

namespace NatsSyncLab.BackendA.Module.Products.Infrastructure.Persistence.Configuration;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(DbConsts.OutboxTable, DbConsts.Schema);

        builder.HasKey(message => message.Id);
        builder.Property(message => message.Id)
            .HasColumnName("id")
            .UseIdentityByDefaultColumn();

        builder.Property(message => message.EventId)
            .HasColumnName("event_id")
            .IsRequired();

        builder.HasIndex(message => message.EventId)
            .HasDatabaseName("ix_outbox_event_id")
            .IsUnique();

        builder.Property(message => message.Subject)
            .HasColumnName("subject")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(message => message.Payload)
            .HasColumnName("payload")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at")
            .IsRequired();

        builder.Property(message => message.PublishedAt)
            .HasColumnName("published_at");

        builder.HasIndex(message => message.Id)
            .HasDatabaseName("ix_outbox_unpublished")
            .HasFilter("\"published_at\" IS NULL");
    }
}
