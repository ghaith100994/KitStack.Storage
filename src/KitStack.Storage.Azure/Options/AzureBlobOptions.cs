using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using KitStack.Abstractions.Options;

namespace KitStack.Storage.Azure.Options;

/// <summary>Options for the Azure Blob Storage provider (bound from Storage:Azure).</summary>
public partial class AzureBlobOptions : IValidatableObject
{
    /// <summary>
    /// Storage account connection string. Leave empty to use <see cref="ServiceUri"/>, or a
    /// <c>BlobServiceClient</c> registered in DI (for example with <c>DefaultAzureCredential</c>).
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>Blob service URI (optionally carrying a SAS token), used when no connection string is set.</summary>
    public Uri? ServiceUri { get; set; }

    /// <summary>Container that holds the files (lower-case, 3-63 characters).</summary>
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>Optional prefix (virtual folder) for every blob name, for example <c>app/</c>.</summary>
    public string? Prefix { get; set; }

    /// <summary>Create the container on first write when it does not exist (private access).</summary>
    public bool CreateContainerIfNotExists { get; set; }

    /// <summary>Access tier for originals: Hot, Cool, Cold or Archive. Empty uses the account default.</summary>
    public string? AccessTier { get; set; }

    /// <summary>Access tier for image variants. Empty falls back to <see cref="AccessTier"/>.</summary>
    public string? VariantAccessTier { get; set; }

    /// <summary>Default lifetime of SAS URLs returned by <c>GetUrlAsync</c>.</summary>
    public int SasUrlExpirationSeconds { get; set; } = 900;

    public ImageProcessingOptions? ImageProcessing { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        var results = new List<ValidationResult>();
        if (string.IsNullOrWhiteSpace(ContainerName) || !ContainerNamePattern().IsMatch(ContainerName))
            results.Add(new ValidationResult("ContainerName must be 3-63 lower-case letters, digits or single hyphens.", [nameof(ContainerName)]));
        if (SasUrlExpirationSeconds <= 0)
            results.Add(new ValidationResult("SasUrlExpirationSeconds must be positive.", [nameof(SasUrlExpirationSeconds)]));
        foreach (var (name, value) in new[] { (nameof(AccessTier), AccessTier), (nameof(VariantAccessTier), VariantAccessTier) })
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Trim().ToUpperInvariant() is not ("HOT" or "COOL" or "COLD" or "ARCHIVE"))
                results.Add(new ValidationResult($"{name} must be Hot, Cool, Cold or Archive.", [name]));
        }

        return results;
    }

    [GeneratedRegex("^[a-z0-9](?!.*--)[a-z0-9-]{1,61}[a-z0-9]$")]
    private static partial Regex ContainerNamePattern();
}
