using Extension.Utilities;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace PhotoGallery.Controls
{
    public partial class LazyImage : UserControl
    {
        private DispatcherTimer? _pollTimer;
        private int _attemptCount;
        private const int MaxAttempts = 20; // Prevent infinite polling

        public static readonly DependencyProperty FilePathProperty = DependencyProperty.Register(
            nameof(FilePath),
            typeof(string),
            typeof(LazyImage),
            new PropertyMetadata("", OnFilePathChanged)
        );

        public string FilePath
        {
            get => (string)GetValue(FilePathProperty);
            set => SetValue(FilePathProperty, value);
        }

        public LazyImage()
        {
            InitializeComponent();
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private static void OnFilePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as LazyImage;
            if (control?.IsLoaded == true && e.NewValue is string newPath)
            {
                control.StartPolling(newPath);
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            ImageNameText.Text = Path.GetFileName(FilePath);
            StartPolling(FilePath);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            StopPolling();
        }

        private void StartPolling(string path)
        {
            StopPolling();

            if (string.IsNullOrWhiteSpace(path))
            {
                SetPlaceholderMode("No file specified");
                return;
            }

            if (!File.Exists(path))
            {
                SetPlaceholderMode("File not found");
                return;
            }

            _attemptCount = 0;

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500) // Slightly longer interval
            };
            _pollTimer.Tick += (s, ev) => CheckDbForThumbnail(path);
            _pollTimer.Start();
        }

        private void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer = null;
            }
        }

        private void SetPlaceholderMode(string message)
        {
            PlaceholderText.Visibility = Visibility.Visible;
            PlaceholderText.Text = message;
            LazyLoadedImage.Visibility = Visibility.Collapsed;
            LazyVideo.Visibility = Visibility.Collapsed;
            PlayOverlay.Visibility = Visibility.Collapsed;
        }

        private void CheckDbForThumbnail(string path)
        {
            _attemptCount++;

            // Stop polling after max attempts to prevent infinite loops
            if (_attemptCount > MaxAttempts)
            {
                SetPlaceholderMode("Thumbnail not available");
                StopPolling();
                return;
            }

            var meta = PhotoDb.GetPhoto(path);
            if (meta?.ThumbnailBlob == null || meta.ThumbnailBlob.Length == 0)
                return;

            bool isVideo = Helper.KnownVideoExtensions.Contains(Path.GetExtension(path));

            BitmapImage? bmp = LoadThumbnailSafely(meta.ThumbnailBlob);
            if (bmp == null)
            {
                SetPlaceholderMode("Invalid thumbnail");
                StopPolling();
                return;
            }

            // Update UI
            PlaceholderText.Visibility = Visibility.Collapsed;
            LazyLoadedImage.Visibility = Visibility.Visible;
            LazyLoadedImage.Source = bmp;
            PlayOverlay.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;

            // Build info text
            string info = BuildInfoText(path, meta);
            ImageNameText.Text = info;

            // Setup click handlers (remove any existing ones first)
            LazyLoadedImage.MouseLeftButtonDown -= ImageClickHandler;
            PlayOverlay.MouseLeftButtonDown -= ImageClickHandler;

            LazyLoadedImage.MouseLeftButtonDown += ImageClickHandler;
            PlayOverlay.MouseLeftButtonDown += ImageClickHandler;

            StopPolling();
        }

        private BitmapImage? LoadThumbnailSafely(byte[] blob)
        {
            try
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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load thumbnail: {ex.Message}");
                return null;
            }
        }

        private string BuildInfoText(string path, PhotoRecord meta)
        {
            var parts = new List<string> { Path.GetFileName(path) };

            if (!string.IsNullOrEmpty(meta.DateSource))
                parts.Add($"{meta.DateTaken:yyyy-MM-dd HH:mm} ({meta.DateSource})");

            if (!string.IsNullOrEmpty(meta.CameraMake) || !string.IsNullOrEmpty(meta.CameraModel))
                parts.Add($"{meta.CameraMake} {meta.CameraModel}".Trim());

            if (!string.IsNullOrEmpty(meta.TagsCsv))
                parts.Add($"Tags: {meta.TagsCsv}");

            return string.Join("\n", parts);
        }

        private void ImageClickHandler(object sender, MouseButtonEventArgs e)
        {
            ShowPreview();
        }

        private void ImageOrVideo_Click(object sender, MouseButtonEventArgs e)
        {
            if (!File.Exists(FilePath)) return;

            bool isVideo = Helper.KnownVideoExtensions.Contains(Path.GetExtension(FilePath));

            if (isVideo)
            {
                // Play video inline
                LazyLoadedImage.Visibility = Visibility.Collapsed;
                PlayOverlay.Visibility = Visibility.Collapsed;
                LazyVideo.Visibility = Visibility.Visible;
                LazyVideo.Source = new Uri(FilePath);
                LazyVideo.Play();
            }
            else
            {
                // Show image preview
                ShowPreview();
            }
        }

        private void ShowPreview()
        {
            try
            {
                var preview = new ImagePreviewWindow(FilePath);
                preview.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open preview: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void LazyVideo_MediaEnded(object sender, RoutedEventArgs e)
        {
            LazyVideo.Position = TimeSpan.Zero;
            LazyVideo.Play();
        }
    }
}