using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace KitStack.Abstractions.DependencyInjection;

/// <summary>
/// Returned by <c>AddKitStackStorage</c>. Provider and integration packages add extension methods to it
/// (<c>AddLocal()</c>, <c>AddS3()</c>, <c>AddAzureBlob()</c>, <c>AddSftp()</c>, <c>AddFtp()</c>, <c>AddFake()</c>,
/// <c>AddEntityFrameworkCoreStore&lt;TContext&gt;()</c>), so an application only references the SDKs it uses.
/// </summary>
public sealed class KitStackStorageBuilder
{
    internal KitStackStorageBuilder(IServiceCollection services, IConfiguration configuration)
    {
        Services = services;
        Configuration = configuration;
    }

    /// <summary>The service collection being configured.</summary>
    public IServiceCollection Services { get; }

    /// <summary>The <c>Storage</c> configuration section (empty when storage was configured in code).</summary>
    public IConfiguration Configuration { get; }

    /// <summary>
    /// The configured <c>Storage:Provider</c> value, for choosing a provider from configuration:
    /// <code>if (storage.IsProvider("S3")) storage.AddS3(); else storage.AddLocal();</code>
    /// </summary>
    public string? Provider => Configuration["Provider"];

    /// <summary>True when <see cref="Provider"/> equals <paramref name="name"/> (case-insensitive).</summary>
    public bool IsProvider(string name) => string.Equals(Provider?.Trim(), name, StringComparison.OrdinalIgnoreCase);
}
