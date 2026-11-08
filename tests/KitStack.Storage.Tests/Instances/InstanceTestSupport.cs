using System.Collections.Concurrent;
using System.Net;
using Amazon.S3;
using Amazon.S3.Model;
using KitStack.Abstractions.Instances;
using KitStack.Abstractions.Interfaces;
using KitStack.Abstractions.Options;
using KitStack.Fakes.Options;
using KitStack.Fakes.Services;
using KitStack.Storage.S3.Options;
using KitStack.Storage.S3.Services;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace KitStack.Storage.Tests.Instances;

/// <summary>Runtime definitions held in memory, like an administration table.</summary>
internal sealed class TestInstanceSource : IStorageInstanceSource
{
    private readonly ConcurrentDictionary<string, StorageInstanceDefinition> _definitions = new(StringComparer.OrdinalIgnoreCase);
    private int _finds;

    public int Finds => Volatile.Read(ref _finds);

    public TimeSpan Delay { get; set; }

    public void Set(StorageInstanceDefinition definition) => _definitions[definition.Name] = definition;

    public void Remove(string name) => _definitions.TryRemove(name, out _);

    public async ValueTask<StorageInstanceDefinition?> FindAsync(string name, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _finds);
        if (Delay > TimeSpan.Zero)
            await Task.Delay(Delay, cancellationToken);
        return _definitions.TryGetValue(name, out var definition) ? definition : null;
    }

    public static StorageInstanceDefinition Definition(string name, string type, string? version, params (string Key, string? Value)[] settings)
        => new(name, type)
        {
            Version = version,
            Settings = StorageInstanceDefinition.CreateSettings(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))),
        };
}

/// <summary>In-memory manager that records its disposal.</summary>
internal sealed class DisposableInMemoryManager(FakeOptions options, StorageOptions storage)
    : InMemoryFileStorageManager(Microsoft.Extensions.Options.Options.Create(options), Microsoft.Extensions.Options.Options.Create(storage)), IAsyncDisposable
{
    private int _disposals;

    public int Disposals => Volatile.Read(ref _disposals);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposals);
        return ValueTask.CompletedTask;
    }
}

/// <summary>Provider type "Counting": in-memory instances whose construction and disposal are counted.</summary>
internal sealed class CountingProviderFactory : StorageProviderFactory<FakeOptions>
{
    public const string Type = "Counting";

    public ConcurrentBag<DisposableInMemoryManager> Created { get; } = [];

    public override string ProviderType => Type;

    public override StorageCapabilities Capabilities => StorageCapabilities.Overwrite;

    protected override IFileStorageManager Create(StorageProviderContext context, FakeOptions options)
    {
        var manager = new DisposableInMemoryManager(options, context.StorageOptions);
        Created.Add(manager);
        return manager;
    }

    protected override Task<HealthCheckResult> ProbeAsync(StorageProviderContext context, FakeOptions options, CancellationToken cancellationToken)
        => Task.FromResult(HealthCheckResult.Healthy());
}

/// <summary>S3 test double: one substitute client per created client, objects kept per bucket.</summary>
internal sealed class FakeS3 : IS3ClientFactory
{
    public ConcurrentDictionary<(string Bucket, string Key), byte[]> Objects { get; } = new();

    public ConcurrentBag<(IAmazonS3 Client, S3TargetOptions Target, S3Options Options)> Clients { get; } = [];

    public IAmazonS3 CreateClient(S3TargetOptions target, S3Options options)
    {
        var client = Substitute.For<IAmazonS3>();
        client.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var request = ci.Arg<PutObjectRequest>();
            using var ms = new MemoryStream();
            request.InputStream.CopyTo(ms);
            Objects[(request.BucketName, request.Key)] = ms.ToArray();
            return new PutObjectResponse();
        });
        client.GetObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Objects.TryGetValue((ci.ArgAt<string>(0), ci.ArgAt<string>(1)), out var body)
                ? Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(body), ContentLength = body.Length })
                : Task.FromException<GetObjectResponse>(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }));
        client.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
            Objects.ContainsKey((ci.ArgAt<string>(0), ci.ArgAt<string>(1)))
                ? Task.FromResult(new GetObjectMetadataResponse())
                : Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound }));
        client.DeleteObjectAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            Objects.TryRemove((ci.ArgAt<string>(0), ci.ArgAt<string>(1)), out _);
            return new DeleteObjectResponse();
        });
        Clients.Add((client, target, options));
        return client;
    }
}

/// <summary>Captures every log message and exception.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public string All => string.Join('\n', Lines);

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => owner.Lines.Enqueue($"{logLevel}: {formatter(state, exception)} {exception}");
    }
}
