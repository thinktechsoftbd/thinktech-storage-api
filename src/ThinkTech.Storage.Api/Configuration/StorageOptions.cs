using System.ComponentModel.DataAnnotations;

namespace ThinkTech.Storage.Api.Configuration;

/// <summary>Where and how uploaded documents are stored on the server.</summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";
    public const long DefaultMaxFileSizeBytes = 100L * 1024 * 1024;

    /// <summary>Root folder for all documents. Relative paths are resolved against the app's content root.</summary>
    [Required(ErrorMessage = "Storage:RootPath is required.")]
    public string RootPath { get; set; } = string.Empty;

    [Range(1, long.MaxValue)]
    public long MaxFileSizeBytes { get; set; } = DefaultMaxFileSizeBytes;

    /// <summary>Allowed file extensions (including the dot). Empty means any extension is accepted.</summary>
    public string[] AllowedExtensions { get; set; } = [];
}
