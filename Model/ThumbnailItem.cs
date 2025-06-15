using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;

namespace PhotoGallery.Model
{
    /// <summary>
    /// ViewModel for a single thumbnail image. Reloads thumbnail blob from DB.
    /// </summary>
    public class ThumbnailItem : INotifyPropertyChanged
    {
        private string _originalPath = string.Empty;
        public string OriginalPath
        {
            get => _originalPath;
            set
            {
                if (_originalPath == value) return;
                _originalPath = value;
                OnPropertyChanged(nameof(OriginalPath));
            }
        }

        private BitmapImage? _thumbnail;
        public BitmapImage? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail == value) return;
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }

        /// <summary>
        /// Reloads the thumbnail image from the database.
        /// </summary>
        public void ReloadFromDb()
        {
            if (string.IsNullOrWhiteSpace(OriginalPath))
            {
                Thumbnail = null;
                return;
            }

            var record = PhotoDb.GetPhoto(OriginalPath);
            if (record?.ThumbnailBlob is { Length: > 0 } blob)
            {
                Thumbnail = LoadBitmap(blob);
            }
            else
            {
                Thumbnail = null;
            }
        }

        /// <summary>
        /// Converts byte[] thumbnail blob to a BitmapImage.
        /// </summary>
        public static BitmapImage LoadBitmap(byte[] blob)
        {
            using var ms = new MemoryStream(blob);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
