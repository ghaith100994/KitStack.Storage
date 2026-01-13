using System;
using System.IO;

namespace KitStack.Storage.S3.Helpers;

public static class S3KeyHelper
{
    public static string NormalizeKey(string prefix, string relativePath, string fileName)
    {
        var p = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix.Trim('/');
        var r = string.IsNullOrWhiteSpace(relativePath) ? string.Empty : relativePath.Trim('/');
        var key = PathCombineForS3(p, r, fileName);
        return key;
    }

    private static string PathCombineForS3(params string[] parts)
    {
        var nonEmpty = Enumerable.Where(parts, s => !string.IsNullOrEmpty(s));
        return string.Join('/', nonEmpty).Replace('\\', '/');
    }
}
