using System.Net;
using System.Net.Http.Headers;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.AspNetCore.Db;
using KitStack.AspNetCore.Extensions;
using KitStack.Fakes.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace KitStack.Storage.Tests.AspNetCore;

public sealed class StoreBackedEndpointTests : IAsyncLifetime
{
    private readonly IFileEntryStore _store = Substitute.For<IFileEntryStore>();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IFileStorageManager _storage = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddKitStackStorage().AddFake();
        builder.Services.AddSingleton(_store);
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthorization();
        _app.MapKitStackStorageFiles("/docs", o => o.CacheControl = "no-store").AllowAnonymous();
        await _app.StartAsync();
        _client = _app.GetTestClient();
        _storage = _app.Services.GetRequiredService<IFileStorageManager>();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    private async Task<IFileEntry> StoreAsync(byte[] content, string name)
    {
        var entry = await _storage.CreateAsync<Invoice>(new MemoryStream(content), name, null, "Docs");
        _store.FindAsync(entry.Id, Arg.Any<CancellationToken>()).Returns(entry);
        _store.FindByLocationAsync(entry.FileLocation, Arg.Any<CancellationToken>()).Returns(entry);
        return entry;
    }

    [Fact]
    public async Task Serves_recorded_files_by_id_with_validators_and_cache_headers()
    {
        var entry = await StoreAsync(TestData.Pdf(), "a.pdf");

        var response = await _client.GetAsync(new Uri($"/docs/{entry.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal($"\"{entry.Checksum}\"", response.Headers.ETag!.Tag);
        Assert.NotNull(response.Content.Headers.LastModified);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains("sandbox", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conditional_and_range_requests_are_honoured()
    {
        var entry = await StoreAsync(TestData.Text("0123456789"), "digits.txt");

        using var notModified = new HttpRequestMessage(HttpMethod.Get, new Uri($"/docs/{entry.FileLocation}", UriKind.Relative));
        notModified.Headers.IfNoneMatch.Add(new EntityTagHeaderValue($"\"{entry.Checksum}\""));
        Assert.Equal(HttpStatusCode.NotModified, (await _client.SendAsync(notModified)).StatusCode);

        using var range = new HttpRequestMessage(HttpMethod.Get, new Uri($"/docs/{entry.FileLocation}", UriKind.Relative));
        range.Headers.Range = new RangeHeaderValue(2, 4);
        var partial = await _client.SendAsync(range);
        Assert.Equal(HttpStatusCode.PartialContent, partial.StatusCode);
        Assert.Equal("234", await partial.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unrecorded_or_deleted_files_are_not_served()
    {
        var unrecorded = await _storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "a.txt", null, "Docs");
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/docs/{unrecorded.FileLocation}", UriKind.Relative))).StatusCode);

        var deleted = await StoreAsync(TestData.Text(), "b.txt");
        deleted.IsDeleted = true;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/docs/{deleted.Id}", UriKind.Relative))).StatusCode);

        var recordedButGone = await StoreAsync(TestData.Text(), "c.txt");
        await _storage.DeleteAsync(recordedButGone);
        recordedButGone.IsDeleted = false;
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(new Uri($"/docs/{recordedButGone.Id}", UriKind.Relative))).StatusCode);
    }

    [Theory]
    [InlineData("image.svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"></svg>", "attachment")]
    [InlineData("notes.txt", "plain text", null)]
    public async Task Svg_is_never_inline_but_plain_text_is(string name, string content, string? disposition)
    {
        var entry = await StoreAsync(TestData.Text(content), name);
        var response = await _client.GetAsync(new Uri($"/docs/{entry.Id}", UriKind.Relative));
        Assert.Equal(disposition, response.Content.Headers.ContentDisposition?.DispositionType);
    }
}

public sealed class StaticFilesOptInTests : IDisposable
{
    private readonly string _folder = "kitstack-static-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        var full = Path.Combine(Directory.GetCurrentDirectory(), _folder);
        if (Directory.Exists(full))
            Directory.Delete(full, recursive: true);
    }

    private async Task<HttpStatusCode> GetAsync(string provider, bool serveAnonymously)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Provider"] = provider,
            ["Storage:Local:Path"] = _folder,
            ["Storage:Local:ServeAnonymously"] = serveAnonymously.ToString(),
        }).Build();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.UseKitStackStorage(config);
        await app.StartAsync();

        await File.WriteAllTextAsync(Path.Combine(Directory.GetCurrentDirectory(), _folder, "hello.txt"), "hi");
        using var client = app.GetTestClient();
        return (await client.GetAsync(new Uri($"/{_folder}/hello.txt", UriKind.Relative))).StatusCode;
    }

    [Fact]
    public async Task Local_files_are_served_anonymously_only_when_opted_in()
    {
        Directory.CreateDirectory(Path.Combine(Directory.GetCurrentDirectory(), _folder));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync("Local", serveAnonymously: false));
        Assert.Equal(HttpStatusCode.NotFound, await GetAsync("S3", serveAnonymously: true));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("Local", serveAnonymously: true));
    }

    [Fact]
    public void Arguments_are_checked()
    {
        Assert.Throws<ArgumentNullException>(() => KitStack.AspNetCore.Extensions.ApplicationBuilderExtensions.UseKitStackStorage(null!, new ConfigurationBuilder().Build()));
        Assert.Throws<ArgumentNullException>(() => FileEndpointRouteBuilderExtensions.MapKitStackStorageFiles(null!));
    }

#pragma warning disable CS0618 // obsolete placeholder kept for 0.1 callers
    [Fact]
    public void Obsolete_database_option_binder_still_binds()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Database:Provider"] = "mongo",
            ["Storage:Database:DatabaseName"] = "files",
        }).Build();
        using var sp = new ServiceCollection().BindSeparateDbContextOptions<object, StorageDbOptions>(config).BuildServiceProvider();
        var options = sp.GetRequiredService<IOptions<StorageDbOptions>>().Value;
        Assert.Equal(("mongo", "files", null), (options.Provider, options.DatabaseName, options.ConnectionString));
    }
#pragma warning restore CS0618
}
