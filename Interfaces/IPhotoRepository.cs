using Extension.Exif;

namespace PhotoGallery.Data
{
    /// <summary>
    /// Abstraction used by the app / view‑models.
    /// </summary>
    public interface IPhotoRepository
    {
        /// <summary>
        /// Returns every PhotoMeta where FilePath.StartsWith(folderPath).
        /// </summary>
        IEnumerable<PhotoMeta> GetAllUnder(string folderPath);
        void Upsert(ExifInfo meta);
        PhotoMeta? Get(string filePath);
    }
}
