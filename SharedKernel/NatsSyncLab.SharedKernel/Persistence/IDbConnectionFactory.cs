using System.Data.Common;

namespace NatsSyncLab.SharedKernel.Persistence;

public interface IDbConnectionFactory
{
    DbConnection CreateConnection();
}
