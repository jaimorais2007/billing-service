namespace BillingService.Domain.Common;

public abstract class VersionedEntity
{
    public int Version { get; protected set; }

    public void MarkPersisted() => Version++;
}
