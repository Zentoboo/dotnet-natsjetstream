namespace NatsSyncLab.BackendB.Module.ProductsReplica.Infrastructure.Messaging;

internal sealed class PermanentEventException(
    string reason,
    string message,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string Reason { get; } = reason;
}
