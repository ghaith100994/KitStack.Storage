using KitStack.Abstractions.Exceptions;
using KitStack.Abstractions.Utilities;

namespace KitStack.Storage.Tests;

public class StoragePathsTests
{
    [Theory]
    [InlineData("../etc")]
    [InlineData("a/../../b")]
    [InlineData("./x")]
    [InlineData("  ")]
    public void Relative_or_empty_categories_are_rejected(string category)
        => Assert.Throws<StorageValidationException>(() => StoragePaths.SanitizeCategory(category));

    [Fact]
    public void Nested_categories_are_kept_and_cleaned()
        => Assert.Equal("Legal/Contracts-2026", StoragePaths.SanitizeCategory(@"Legal\Contracts 2026"));

    [Fact]
    public void Segments_keep_unicode_letters_and_drop_separators()
    {
        Assert.Equal("عقد-الإيجار", StoragePaths.SanitizeSegment("عقد الإيجار"));
        Assert.Equal("a-b", StoragePaths.SanitizeSegment("a/../b".Replace(".", "", StringComparison.Ordinal)));
        Assert.Equal("file", StoragePaths.SanitizeSegment("..."));
    }

    [Theory]
    [InlineData("photo.JPG", ".jpg")]
    [InlineData("archive.tar.gz", ".gz")]
    [InlineData("noext", "")]
    [InlineData("weird.p%p", "")]
    public void Extensions_are_normalised(string fileName, string expected)
        => Assert.Equal(expected, StoragePaths.GetExtension(fileName));
}
