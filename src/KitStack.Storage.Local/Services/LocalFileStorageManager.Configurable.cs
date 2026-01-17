using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.Options;

namespace KitStack.Storage.Local.Services;

public partial class LocalFileStorageManager : IConfigurableProvider
{
    void IConfigurableProvider.UpdateOptions(StorageProvider provider)
    {
        if (provider == null) return;

        try
        {
            // provider.Options is expected to be a LocalOptions instance (or serializable to one)
            if (provider.Options is LocalOptions local)
            {
                _option = local;
            }
            else if (provider.Options != null)
            {
                // try to map using a simple cast via JSON (best-effort): if it's a string or a JObject in consumers
                try
                {
                    var json = System.Text.Json.JsonSerializer.Serialize(provider.Options);
                    var des = System.Text.Json.JsonSerializer.Deserialize<LocalOptions>(json);
                    if (des != null) _option = des;
                }
                catch
                {
                    // ignore mapping errors
                }
            }

            EnsureInitializedFromOptions();
        }
        catch
        {
            // best-effort
        }
    }
}
