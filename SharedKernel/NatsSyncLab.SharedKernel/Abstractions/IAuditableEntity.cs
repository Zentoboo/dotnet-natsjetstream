namespace NatsSyncLab.SharedKernel.Abstractions;

public interface IAuditableEntity
{
    string? CreatedUsername { get; set; }

    DateTimeOffset CreatedOn { get; set; }

    string? UpdatedUsername { get; set; }

    DateTimeOffset? UpdatedOn { get; set; }
}
