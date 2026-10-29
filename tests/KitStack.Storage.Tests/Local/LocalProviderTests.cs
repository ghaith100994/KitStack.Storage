using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Models;
using KitStack.Storage.Local.Extensions;
using KitStack.Storage.Local.HealthChecks;
using KitStack.Storage.Local.Options;
using KitStack.Storage.Local.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace KitStack.Storage.Tests.Local;

public sealed class LocalProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kitstack-local-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        if (File.Exists(_root))
            File.Delete(_root);
    }

    private static HealthCheckContext Context(IHealthCheck check)
        => new() { Registration = new HealthCheckRegistration("local", check, HealthStatus.Unhealthy, null) };

    [Fact]
    public async Task Health_check_is_healthy_for_a_writable_path_and_leaves_nothing_behind()
    {
        var check = new LocalFileHealthCheck(MsOptions.Create(new LocalOptions { Path = _root }));

        var result = await check.CheckHealthAsync(Context(check));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains(_root, result.Description, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task Health_check_is_unhealthy_when_the_path_is_not_a_directory()
    {
        await File.WriteAllTextAsync(_root, "a file where the directory should be");
        var check = new LocalFileHealthCheck(MsOptions.Create(new LocalOptions { Path = _root }));

        var result = await check.CheckHealthAsync(Context(check));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.NotNull(result.Exception);
    }

    [Fact]
    public async Task Health_check_reports_cancellation()
    {
        var check = new LocalFileHealthCheck(MsOptions.Create(new LocalOptions { Path = _root }));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await check.CheckHealthAsync(Context(check), cts.Token);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal("Health check cancelled", result.Description);
        Assert.Throws<ArgumentNullException>(() => new LocalFileHealthCheck(null!));
    }

    [Fact]
    public void Base_directory_is_created_only_when_asked()
    {
        _ = new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root, EnsureBasePathExists = false }));
        Assert.False(Directory.Exists(_root));

        _ = new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root }));
        Assert.True(Directory.Exists(_root));
        Assert.Throws<ArgumentNullException>(() => new LocalFileStorageManager(null!));
    }

    [Fact]
    public async Task Absolute_paths_inside_the_base_are_accepted()
    {
        var storage = new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root }));
        var entry = await storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");

        var absolute = new FileEntry { FileLocation = Path.Combine(_root, entry.FileLocation) };

        Assert.True(await storage.ExistsAsync(absolute));
        Assert.Equal(Path.Combine(_root, entry.FileLocation).Replace('\\', '/'), entry.StoragePath);
        await Assert.ThrowsAsync<StorageValidationException>(() => storage.ExistsAsync(new FileEntry { FileLocation = "/etc/passwd" }));
    }

    [Fact]
    public async Task Missing_directories_are_not_found_rather_than_errors()
    {
        var storage = new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root }));
        var missing = new FileEntry { FileLocation = "no/such/dir/file.txt" };

        Assert.False(await storage.ExistsAsync(missing));
        await storage.DeleteAsync(missing);
        await Assert.ThrowsAsync<StorageNotFoundException>(() => storage.OpenReadAsync(missing));
    }

    [Fact]
    public async Task Urls_escape_each_path_segment()
    {
        var storage = new LocalFileStorageManager(MsOptions.Create(new LocalOptions { Path = _root, PublicBaseUrl = "https://h/files" }));

        var url = await storage.GetUrlAsync(new FileEntry { FileLocation = "عقود/a b#.pdf" });

        Assert.Equal("https://h/files/%D8%B9%D9%82%D9%88%D8%AF/a%20b%23.pdf", url!.AbsoluteUri);
    }

    [Fact]
    public void Registration_binds_Storage_Local_and_adds_a_health_check()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Local:Path"] = _root,
            ["Storage:Local:PublicBaseUrl"] = "https://h/files",
        }).Build();
        var services = new ServiceCollection();
        services.AddKitStackStorage(config).AddLocal(o => o.ServeAnonymously = true);

        using var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalOptions>>().Value;
        Assert.Equal((_root, "https://h/files", true), (options.Path, options.PublicBaseUrl, options.ServeAnonymously));
        Assert.IsType<LocalFileStorageManager>(sp.GetRequiredService<IFileStorageManager>());
        var health = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;
        Assert.Contains(health.Registrations, r => r.Name == "kitstack_local");
    }

    [Fact]
    public void Registration_with_an_options_instance()
    {
        using var sp = new ServiceCollection().AddLocalStorageManager(new LocalOptions { Path = _root }).BuildServiceProvider();
        Assert.IsType<LocalFileStorageManager>(sp.GetRequiredService<IFileStorageManager>());
        Assert.NotNull(sp.GetRequiredService<LocalFileHealthCheck>());
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddLocalStorageManager((IConfiguration)null!));
        Assert.Throws<ArgumentNullException>(() => ((KitStackStorageBuilder)null!).AddLocal());
    }
}
