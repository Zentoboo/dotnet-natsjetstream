namespace NatsSyncLab.SharedKernel.Abstractions;

public interface ISoftDelete
{
    bool IsDeleted { get; set; }
}
