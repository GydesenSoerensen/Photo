using Extension.Exif;
using Extension.Utilities;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoGallery
{
    public partial class ImagePreviewWindow : Window
    {
        private readonly string _filePath;
        private double _zoomFactor = 1.0;
        private const double ZoomIncrement = 0.2;
        private const double MinZoom = 0.1;
        private const double MaxZoom = 5.0;

        public ImagePreviewWindow(string filePath)
        {
            InitializeComponent();
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

            Title = $"Preview - {Path.GetFileName(filePath)}";

            // Set reasonable initial size
            Width = 800;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Loaded += Window_Loaded;
            KeyDown += ImagePreviewWindow_KeyDown;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (!File.Exists(_filePath))
            {
                MessageBox.Show("File not found: " + _filePath, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
                return;
            }

            try
            {
                if (IsVideoFile(_filePath))
                {
                    LoadVideo();
                    ZoomControls.Visibility = Visibility.Collapsed; // Hide zoom for videos
                }
                else
                {
                    LoadImage();
                    ZoomControls.Visibility = Visibility.Visible; // Show zoom for images
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        #region Zoom Functionality

        private void MainScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                if (e.Delta > 0)
                    ZoomIn();
                else
                    ZoomOut();

                e.Handled = true;
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            ZoomIn();
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            ZoomOut();
        }

        private void ZoomReset_Click(object sender, RoutedEventArgs e)
        {
            ResetZoom();
        }

        private void ZoomIn()
        {
            _zoomFactor = Math.Min(_zoomFactor + ZoomIncrement, MaxZoom);
            ApplyZoom();
        }

        private void ZoomOut()
        {
            _zoomFactor = Math.Max(_zoomFactor - ZoomIncrement, MinZoom);
            ApplyZoom();
        }

        private void ResetZoom()
        {
            _zoomFactor = 1.0;
            ApplyZoom();
        }

        private void ApplyZoom()
        {
            ScaleTransform.ScaleX = _zoomFactor;
            ScaleTransform.ScaleY = _zoomFactor;
            ZoomPercentage.Text = $"{_zoomFactor * 100:F0}%";
        }

        #endregion

        private void LoadVideo()
        {
            VideoPlayer.Visibility = Visibility.Visible;
            FullImage.Visibility = Visibility.Collapsed;

            VideoPlayer.Source = new Uri(_filePath);
            VideoPlayer.Play();
        }

        private void LoadImage()
        {
            FullImage.Visibility = Visibility.Visible;
            VideoPlayer.Visibility = Visibility.Collapsed;

            var bitmap = LoadImageWithCache(_filePath);
            FullImage.Source = bitmap;

            // Apply EXIF rotation using our refactored library
            var rotation = GetRotationFromExif(_filePath);
            if (rotation != null)
                FullImage.LayoutTransform = rotation;
        }

        private BitmapImage LoadImageWithCache(string filePath)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(filePath);

                // Only decode to a reasonable size for very large images
                // This prevents memory issues while maintaining quality
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > 10 * 1024 * 1024) // 10MB+
                {
                    bitmap.DecodePixelWidth = 1920; // Max width for large files
                }

                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to load image: {ex.Message}", ex);
            }
        }

        private static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path);
            return Helper.KnownVideoExtensions.Contains(ext);
        }

        private static RotateTransform? GetRotationFromExif(string filePath)
        {
            try
            {
                // Use our refactored EXIF reader instead of manual parsing
                var exifInfo = ExifReader.TryRead(filePath);
                if (exifInfo != null)
                {
                    return exifInfo.Orientation switch
                    {
                        3 => new RotateTransform(180),
                        6 => new RotateTransform(90),
                        8 => new RotateTransform(270),
                        _ => null,
                    };
                }
            }
            catch
            {
                // Fallback to manual parsing if the EXIF reader fails
                return GetRotationFromExifManual(filePath);
            }
            return null;
        }

        private static RotateTransform? GetRotationFromExifManual(string filePath)
        {
            try
            {
                using var img = System.Drawing.Image.FromFile(filePath);
                const int OrientationId = 0x0112;
                if (img.PropertyIdList.Contains(OrientationId))
                {
                    var orientation = (int)img.GetPropertyItem(OrientationId).Value[0];
                    return orientation switch
                    {
                        3 => new RotateTransform(180),
                        6 => new RotateTransform(90),
                        8 => new RotateTransform(270),
                        _ => null,
                    };
                }
            }
            catch { }
            return null;
        }

        private void VideoPlayer_MediaEnded(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Position = TimeSpan.Zero;
            VideoPlayer.Play(); // Loop video
        }

        private void ImagePreviewWindow_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // Close on Escape key
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
            // Toggle fullscreen on F11
            else if (e.Key == System.Windows.Input.Key.F11)
            {
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal
                    : WindowState.Maximized;
            }
            // Zoom controls
            else if (e.Key == System.Windows.Input.Key.Add || e.Key == System.Windows.Input.Key.OemPlus)
            {
                ZoomIn();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.Subtract || e.Key == System.Windows.Input.Key.OemMinus)
            {
                ZoomOut();
                e.Handled = true;
            }
            else if (e.Key == System.Windows.Input.Key.D0 || e.Key == System.Windows.Input.Key.NumPad0)
            {
                ResetZoom();
                e.Handled = true;
            }
            // Pause/play video on Space
            else if (e.Key == System.Windows.Input.Key.Space && VideoPlayer.Visibility == Visibility.Visible)
            {
                try
                {
                    if (VideoPlayer.LoadedBehavior == System.Windows.Controls.MediaState.Manual)
                    {
                        // Check if we can pause/play
                        if (VideoPlayer.CanPause)
                        {
                            // Simple toggle - if at end, restart, otherwise pause
                            if (VideoPlayer.Position >= VideoPlayer.NaturalDuration.TimeSpan)
                            {
                                VideoPlayer.Position = TimeSpan.Zero;
                                VideoPlayer.Play();
                            }
                            else
                            {
                                VideoPlayer.Pause();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Log the error but don't crash
                    System.Diagnostics.Debug.WriteLine($"Video control error: {ex.Message}");
                }
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop video and release resources
            try
            {
                if (VideoPlayer.Source != null)
                {
                    VideoPlayer.Stop();
                    VideoPlayer.Source = null;
                }
            }
            catch
            {
                // Ignore cleanup errors
            }

            base.OnClosed(e);
        }
    }
}