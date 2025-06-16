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
        private const int MaxAttempts = 10; // Reduced from 20 to limit database calls
        private const int PollingIntervalMs = 1000; // Increased from 500ms to reduce frequency
        private bool _isLoaded = false;
        private CancellationTokenSource? _loadCancellation;

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
            _isLoaded = true;
            ImageNameText.Text = Path.GetFileName(FilePath);
            StartPolling(FilePath);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            _isLoaded = false;
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

            // FIXED: Reduced polling frequency and added immediate check
            _ = CheckDbForThumbnailAsync(path); // Immediate check

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(PollingIntervalMs)
            };
            _pollTimer.Tick += async (s, ev) => await CheckDbForThumbnailAsync(path);
            _pollTimer.Start();
        }

        private void StopPolling()
        {
            if (_pollTimer != null)
            {
                _pollTimer.Stop();
                _pollTimer = null;
            }

            _loadCancellation?.Cancel();
            _loadCancellation = null;
        }

        private void SetPlaceholderMode(string message)
        {
            if (!_isLoaded) return;

            PlaceholderText.Visibility = Visibility.Visible;
            PlaceholderText.Text = message;
            LazyLoadedImage.Visibility = Visibility.Collapsed;
            LazyVideo.Visibility = Visibility.Collapsed;
            PlayOverlay.Visibility = Visibility.Collapsed;
        }

        // FIXED: Made async to prevent UI blocking
        private async Task CheckDbForThumbnailAsync(string path)
        {
            if (!_isLoaded) return;

            _attemptCount++;

            // Stop polling after max attempts to prevent infinite loops
            if (_attemptCount > MaxAttempts)
            {
                SetPlaceholderMode("Thumbnail not available");
                StopPolling();
                return;
            }

            try
            {
                // Cancel any previous load operation
                _loadCancellation?.Cancel();
                _loadCancellation = new CancellationTokenSource();

                // FIXED: Load metadata on background thread to avoid blocking UI
                var meta = await Task.Run(() => PhotoDb.GetPhoto(path), _loadCancellation.Token);

                if (_loadCancellation.Token.IsCancellationRequested)
                    return;

                if (meta?.ThumbnailBlob == null || meta.ThumbnailBlob.Length == 0)
                    return;

                bool isVideo = Helper.KnownVideoExtensions.Contains(Path.GetExtension(path));

                // FIXED: Load bitmap on background thread
                BitmapImage? bmp = await LoadThumbnailSafelyAsync(meta.ThumbnailBlob);

                if (_loadCancellation.Token.IsCancellationRequested)
                    return;

                if (bmp == null)
                {
                    SetPlaceholderMode("Invalid thumbnail");
                    StopPolling();
                    return;
                }

                // FIXED: Build info text on background thread
                string info = await Task.Run(() => BuildInfoText(path, meta), _loadCancellation.Token);

                if (_loadCancellation.Token.IsCancellationRequested)
                    return;

                // Update UI on UI thread
                if (Dispatcher.CheckAccess())
                {
                    UpdateUI(bmp, isVideo, info);
                }
                else
                {
                    await Dispatcher.InvokeAsync(() => UpdateUI(bmp, isVideo, info));
                }

                StopPolling();
            }
            catch (OperationCanceledException)
            {
                // Silently handle cancellation
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error checking thumbnail: {ex.Message}");
                // Continue polling on error, but don't show error to user
            }
        }

        private void UpdateUI(BitmapImage bmp, bool isVideo, string info)
        {
            if (!_isLoaded) return;

            PlaceholderText.Visibility = Visibility.Collapsed;
            LazyLoadedImage.Visibility = Visibility.Visible;
            LazyLoadedImage.Source = bmp;
            PlayOverlay.Visibility = isVideo ? Visibility.Visible : Visibility.Collapsed;
            ImageNameText.Text = info;

            // Setup click handlers (remove any existing ones first)
            LazyLoadedImage.MouseLeftButtonDown -= ImageClickHandler;
            PlayOverlay.MouseLeftButtonDown -= ImageClickHandler;

            LazyLoadedImage.MouseLeftButtonDown += ImageClickHandler;
            PlayOverlay.MouseLeftButtonDown += ImageClickHandler;
        }

        // FIXED: Made async to prevent UI blocking
        private async Task<BitmapImage?> LoadThumbnailSafelyAsync(byte[] blob)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var ms = new MemoryStream(blob);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze(); // Important: Freeze for cross-thread access
                    return bmp;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Failed to load thumbnail: {ex.Message}");
                    return null;
                }
            }).ConfigureAwait(false);
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