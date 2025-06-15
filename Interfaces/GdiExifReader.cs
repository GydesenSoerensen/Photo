using Extension.Exif;
using Serilog;
using System.IO;

namespace PhotoGallery.Imaging
{
    public sealed class GdiExifReader : IExifReader
    {
        public ExifInfo Read(string path)
        {
            try
            {
                return ExifReader.Read(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Exif] Error reading file: {Path}", path);

                // Return error ExifInfo with correct constructor signature
                return new ExifInfo(
                    fullPath: path,
                    source: "Error",
                    cameraMake: "",
                    cameraModel: "",
                    orientation: 1,
                    keywords: Array.Empty<string>(),
                    thumbnail: null,
                    allTags: new Dictionary<string, string>
                    {
                        ["Error"] = ex.Message,
                        ["FileCreationTime"] = File.GetCreationTime(path).ToString("yyyy-MM-dd HH:mm:ss")
                    }
                );
            }
        }

        /// <summary>
        /// Async version for better performance.
        /// </summary>
        public async Task<ExifInfo> ReadAsync(string path)
        {
            try
            {
                return await ExifReader.ReadAsync(path);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Exif] Error reading file async: {Path}", path);

                return new ExifInfo(
                    fullPath: path,
                    source: "Error",
                    cameraMake: "",
                    cameraModel: "",
                    orientation: 1,
                    keywords: Array.Empty<string>(),
                    thumbnail: null,
                    allTags: new Dictionary<string, string>
                    {
                        ["Error"] = ex.Message,
                        ["FileCreationTime"] = File.GetCreationTime(path).ToString("yyyy-MM-dd HH:mm:ss")
                    }
                );
            }
        }
    }
}