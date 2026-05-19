using System.Runtime.Serialization;

namespace SmartCon.Core.Models.FamilyManager;

public sealed class DbAccessDeniedException : Exception
{
    public string DbName { get; }
    public string OwnerDisplayName { get; }

    public DbAccessDeniedException(string dbName, string ownerDisplayName)
        : base($"Access to database \"{dbName}\" is restricted by the owner.")
    {
        DbName = dbName;
        OwnerDisplayName = ownerDisplayName;
    }

    public DbAccessDeniedException(string message, Exception innerException)
        : base(message, innerException)
    {
        DbName = string.Empty;
        OwnerDisplayName = string.Empty;
    }

#if NET48
    private DbAccessDeniedException(SerializationInfo info, StreamingContext context)
        : base(info, context)
    {
        DbName = info.GetString(nameof(DbName)) ?? string.Empty;
        OwnerDisplayName = info.GetString(nameof(OwnerDisplayName)) ?? string.Empty;
    }

    public override void GetObjectData(SerializationInfo info, StreamingContext context)
    {
        base.GetObjectData(info, context);
        info.AddValue(nameof(DbName), DbName);
        info.AddValue(nameof(OwnerDisplayName), OwnerDisplayName);
    }
#endif
}
