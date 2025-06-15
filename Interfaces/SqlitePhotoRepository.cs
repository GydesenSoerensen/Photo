using Extension.Exif;

namespace PhotoGallery.Data
{
    /// <summary>
    /// Plain C# record that holds everything we persist for one photo.
    /// DateTaken is stored as Unix epoch seconds.
    /// </summary>
    public record PhotoMeta(
        string FilePath,
        long DateTakenEpoch,
        string DateSource,
        string CameraMake,
        string CameraModel,
        int Orientation,
        string TagsCsv,
        byte[]? ThumbnailBlob
    )
    {
        /// <summary>
        /// Convenience property to get DateTime from epoch.
        /// </summary>
        public DateTime DateTaken => DateTimeOffset.FromUnixTimeSeconds(DateTakenEpoch).DateTime;
    }

    /// <summary>
    /// Concrete implementation that delegates to the static <see cref="PhotoDb"/>.
    /// Registered as a singleton in DI.
    /// </summary>
    public sealed class SqlitePhotoRepository : IPhotoRepository
    {
        /// <summary>
        /// Insert or update a photo record in SQLite.
        /// </summary>
        public void Upsert(ExifInfo exifInfo)
        {
            if (exifInfo == null)
                throw new ArgumentNullException(nameof(exifInfo));

            PhotoDb.UpsertPhoto(exifInfo);
        }

        /// <summary>
        /// Retrieve a photo record, or null if not found.
        /// </summary>
        public PhotoMeta? Get(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return null;

            var record = PhotoDb.GetPhoto(filePath);
            if (record == null)
                return null;

            return new PhotoMeta(
                FilePath: record.FilePath,
                DateTakenEpoch: record.DateTakenEpoch,
                DateSource: record.DateSource,
                CameraMake: record.CameraMake,
                CameraModel: record.CameraModel,
                Orientation: record.Orientation,
                TagsCsv: record.TagsCsv, // Fixed: This is already a string, no conversion needed
                ThumbnailBlob: record.ThumbnailBlob
            );
        }

        /// <summary>
        /// Get all photos under a specific folder path.
        /// </summary>
        public IEnumerable<PhotoMeta> GetAllUnder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return Enumerable.Empty<PhotoMeta>();

            return PhotoDb.GetAllUnder(folderPath);
        }

        /// <summary>
        /// Check if a photo exists in the database.
        /// </summary>
        public bool Exists(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return false;

            return PhotoDb.Exists(filePath);
        }

        /// <summary>
        /// Get all photos with thumbnails for a folder (for faster UI loading).
        /// </summary>
        public IEnumerable<PhotoMeta> GetAllWithThumbnails(string folderPath)
        {
            return GetAllUnder(folderPath)
                .Where(photo => photo.ThumbnailBlob?.Length > 0);
        }

        /// <summary>
        /// Get photos by date range.
        /// </summary>
        public IEnumerable<PhotoMeta> GetByDateRange(DateTime startDate, DateTime endDate, string? folderPath = null)
        {
            var startEpoch = new DateTimeOffset(startDate).ToUnixTimeSeconds();
            var endEpoch = new DateTimeOffset(endDate).ToUnixTimeSeconds();

            var photos = string.IsNullOrEmpty(folderPath)
                ? PhotoDb.GetAllUnder("") // Get all photos
                : PhotoDb.GetAllUnder(folderPath);

            return photos.Where(p => p.DateTakenEpoch >= startEpoch && p.DateTakenEpoch <= endEpoch);
        }

        /// <summary>
        /// Get photos by camera make/model.
        /// </summary>
        public IEnumerable<PhotoMeta> GetByCamera(string? make = null, string? model = null, string? folderPath = null)
        {
            var photos = string.IsNullOrEmpty(folderPath)
                ? PhotoDb.GetAllUnder("")
                : PhotoDb.GetAllUnder(folderPath);

            return photos.Where(p =>
                (string.IsNullOrEmpty(make) || p.CameraMake.Contains(make, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(model) || p.CameraModel.Contains(model, StringComparison.OrdinalIgnoreCase))
            );
        }

        /// <summary>
        /// Search photos by tags.
        /// </summary>
        public IEnumerable<PhotoMeta> SearchByTags(string[] tags, string? folderPath = null)
        {
            if (tags == null || tags.Length == 0)
                return Enumerable.Empty<PhotoMeta>();

            var photos = string.IsNullOrEmpty(folderPath)
                ? PhotoDb.GetAllUnder("")
                : PhotoDb.GetAllUnder(folderPath);

            return photos.Where(p =>
                !string.IsNullOrEmpty(p.TagsCsv) &&
                tags.Any(tag => p.TagsCsv.Contains(tag, StringComparison.OrdinalIgnoreCase))
            );
        }
    }
}