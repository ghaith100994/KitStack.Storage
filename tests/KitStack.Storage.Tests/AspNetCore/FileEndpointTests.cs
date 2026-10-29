using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using KitStack.Abstractions.DependencyInjection;
using KitStack.Abstractions.Interfaces;
using KitStack.AspNetCore.Extensions;
using KitStack.Fakes.Extensions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace KitStack.Storage.Tests.AspNetCore;

public sealed class FileEndpointTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private IFileStorageManager _storage = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddKitStackStorage().AddFake();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", _ => { });
        builder.Services.AddAuthorization();

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapKitStackStorageFiles("/files", o => o.Authorize = (_, entry) => ValueTask.FromResult(!entry.FileName.Contains("forbidden", StringComparison.Ordinal)));
        await _app.StartAsync();

        _client = _app.GetTestClient();
        _storage = _app.Services.GetRequiredService<IFileStorageManager>();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Anonymous_requests_are_rejected()
    {
        var entry = await _storage.CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "a.png", null, "Docs");
        var response = await _client.GetAsync(new Uri($"/files/{entry.FileLocation}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_users_get_the_file_inline_with_safe_headers()
    {
        var entry = await _storage.CreateAsync<Invoice>(new MemoryStream(TestData.Png()), "a.png", null, "Docs");

        var response = await SendAsync($"/files/{entry.FileLocation}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Null(response.Content.Headers.ContentDisposition);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(TestData.Png(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Active_content_is_sent_as_an_attachment()
    {
        var entry = await _storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text("<html><script>alert(1)</script></html>")), "page.html", null, "Docs");

        var response = await SendAsync($"/files/{entry.FileLocation}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
    }

    [Fact]
    public async Task Missing_files_and_traversal_return_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync("/files/Docs/Invoice/Others/missing.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync($"/files/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Per_file_authorization_can_forbid()
    {
        var entry = await _storage.CreateAsync<Invoice>(new MemoryStream(TestData.Text()), "forbidden.txt", null, "Docs");
        Assert.Equal(HttpStatusCode.Forbidden, (await SendAsync($"/files/{entry.FileLocation}")).StatusCode);
    }

    private Task<HttpResponseMessage> SendAsync(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Add("X-User", "alice");
        return _client.SendAsync(request);
    }

    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User", out var user))
                return Task.FromResult(AuthenticateResult.NoResult());
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, user.ToString())], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
