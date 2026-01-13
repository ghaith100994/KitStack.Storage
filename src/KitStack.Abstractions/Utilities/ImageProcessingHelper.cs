using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace KitStack.Abstractions.Utilities
{
    /// <summary>
    /// Utility helpers for image detection and processing shared across providers.
    /// Providers can call these helpers to determine type folders and create resized JPEG variants.
    /// </summary>
    public static class ImageProcessingHelper
    {
        /// <summary>
        /// Common image file extensions (lower/upper-insensitive).
        /// </summary>
        public static readonly IReadOnlyCollection<string> ImageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff"
        };

        /// <summary>
        /// Return a simple folder name for the file type based on extension ("Images" or "Others").
        /// </summary>
        public static string GetFileTypeFolder(string extension)
            => IsImageExtension(extension) ? "Images" : "Others";

        /// <summary>
        /// True when the given extension matches a known image extension.
        /// Accepts values like ".jpg" or "jpg".
        /// </summary>
        public static bool IsImageExtension(string extension)
        {
            if (string.IsNullOrWhiteSpace(extension)) return false;
            if (!extension.StartsWith('.')) 
                extension = "." + extension;
            return ImageExtensions.Contains(extension);
        }

        /// <summary>
        /// Create a resized JPEG variant from the provided source stream.
        /// - Rewinds the source stream when possible.
        /// - Resizes preserving aspect ratio so both width and height are <= specified max.
        /// - Saves the output as JPEG using the requested quality.
        /// </summary>
        /// <param name="sourceStream">Input stream (will not be disposed).</param>
        /// <param name="destinationFullPath">Absolute path to write the JPEG file to.</param>
        /// <param name="maxWidth">Maximum width in pixels.</param>
        /// <param name="maxHeight">Maximum height in pixels.</param>
        /// <param name="quality">JPEG quality 1..100.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public static async Task CreateResizedJpegToStreamAsync(Stream sourceStream, Stream destinationStream, int maxWidth, int maxHeight, int quality, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
            ArgumentNullException.ThrowIfNull(sourceStream);
            ArgumentNullException.ThrowIfNull(destinationStream);

            // Rewind if possible (so callers can reuse the same stream)
            if (sourceStream.CanSeek)
                sourceStream.Seek(0, SeekOrigin.Begin);

            // Load image (ImageSharp will detect format)
            using var image = await Image.LoadAsync(sourceStream, cancellationToken).ConfigureAwait(false);

            var resizeNeeded = image.Width > maxWidth || image.Height > maxHeight;

            if (resizeNeeded)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(maxWidth, maxHeight),
                    Mode = ResizeMode.Max
                }));
            }

            var encoder = new JpegEncoder { Quality = Math.Clamp(quality, 1, 100) };
            await image.SaveAsJpegAsync(destinationStream, encoder, cancellationToken).ConfigureAwait(false);
            if (destinationStream.CanSeek)
                destinationStream.Seek(0, SeekOrigin.Begin);
        }
    }
}