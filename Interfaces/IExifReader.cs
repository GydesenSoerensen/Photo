using Extension.Exif;

namespace PhotoGallery.Imaging
{
    /// <summary>
    /// Interface for reading EXIF metadata from image and video files.
    /// </summary>
    public interface IExifReader
    {
        /// <summary>
        /// Reads EXIF metadata from a file synchronously.
        /// </summary>
        /// <param name="filePath">Path to the media file</param>
        /// <returns>ExifInfo containing extracted metadata</returns>
        /// <exception cref="ArgumentException">Thrown when filePath is invalid</exception>
        /// <exception cref="FileNotFoundException">Thrown when file does not exist</exception>
        ExifInfo Read(string filePath);

        /// <summary>
        /// Reads EXIF metadata from a file asynchronously.
        /// </summary>
        /// <param name="filePath">Path to the media file</param>
        /// <returns>Task containing ExifInfo with extracted metadata</returns>
        /// <exception cref="ArgumentException">Thrown when filePath is invalid</exception>
        /// <exception cref="FileNotFoundException">Thrown when file does not exist</exception>
        Task<ExifInfo> ReadAsync(string filePath);
    }
}