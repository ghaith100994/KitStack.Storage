using System.Collections.Generic;
using KitStack.Storage.Abstractions.Interfaces;
using KitStack.Storage.Fakes.Models;

namespace KitStack.Storage.Fakes.Contracts;

/// <summary>
/// Test helper interface that allows tests to inspect stored files in the fake store.
/// Implementations are optional; InMemoryFileStorageManager implements this interface.
/// </summary>
public interface IFakeFileStore
{
    IReadOnlyCollection<FakeStoredFile> ListFiles();

    bool TryGetFile(string fileLocation, out FakeStoredFile? file);

    void Clear(); // remove all files, useful for test teardown
}