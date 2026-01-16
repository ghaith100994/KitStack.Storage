namespace KitStack.Abstractions.Models;

public class StorageProviderRegistration
{
    public StorageProvider Provider { get; }

    public Type? ManagerType { get; }

    public StorageProviderRegistration(StorageProvider provider, Type? managerType = null)
    {
        Provider = provider;
        ManagerType = managerType;
    }
}
