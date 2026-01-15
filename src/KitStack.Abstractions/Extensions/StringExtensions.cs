using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KitStack.Abstractions.Extensions;

public static class StringExtensions
{
    public static bool HasValue(this string? str) => !string.IsNullOrWhiteSpace(str) && !string.IsNullOrEmpty(str);

    public static bool IsEmpty(this string? str) => string.IsNullOrWhiteSpace(str) || string.IsNullOrEmpty(str);
}
