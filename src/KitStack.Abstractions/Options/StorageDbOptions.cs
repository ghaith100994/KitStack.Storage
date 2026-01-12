namespace KitStack.Abstractions.Options;

/// <summary>
/// Options used to configure the optional database used for storing file metadata.
/// Location in configuration: Storage:Database
/// </summary>
public class StorageDbOptions
{
    /// <summary>
    /// Examples: "mongo", "mssql", "postgres", "mysql", "sqlite".
    /// </summary>
    public string? Provider { get; set; }

    /// <summary>
    /// Connection string for the chosen provider.
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// For document stores (e.g. Mongo): the database name to use.
    /// For relational stores this can be ignored.
    /// </summary>
    public string? DatabaseName { get; set; }
}