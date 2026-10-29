namespace KitStack.Storage.Tests;

/// <summary>
/// Tests that count files in the temp directory. xUnit runs non-parallel collections after all parallel ones,
/// so no other test creates temp files while these run.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TempFileCollection
{
    public const string Name = "Temp files";
}
