using Extension.Exif;
using Extension.Utilities;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoGallery
{
    public partial class ImagePreviewWindow : Window
    {
        private readonly string _filePath;

        public ImagePreviewWindow(string filePath)
        {
            InitializeComponent();
            _filePath = filePath ?? throw new ArgumentNullException(nameof(filePath));

            Title = $"Preview - {Path.GetFileName(filePath)}";
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
                }
                else
                {
                    LoadImage();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        }

        private void LoadVideo()
        {
            VideoPlayer.Visibility = Visibility.Visible;
            FullImage.Visibility = Visibility.Collapsed;

            VideoPlayer.Source = new Uri(_filePath);
            VideoPlayer.Play();

            // Auto-resize video player
            SizeChanged += (_, _) =>
            {
                if (ActualHeight > 0)
                    VideoPlayer.Height = ActualHeight - 50; // Leave some margin
            };
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

            // Auto-resize image
            SizeChanged += (_, _) =>
            {
                if (ActualHeight > 0)
                    FullImage.Height = ActualHeight - 50; // Leave some margin
            };
        }

        private BitmapImage LoadImageWithCache(string filePath)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(filePath);

            // Decode to a reasonable size to save memory for large images
            bitmap.DecodePixelWidth = 1920; // Max width for preview

            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        private static bool IsVideoFile(string path)
        {
            string ext = Path.GetExtension(path);
            return Helper.KnownVideoExtensions.Contains(ext); // Fixed: Use the new property
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
            // Pause/play video on Space
            else if (e.Key == System.Windows.Input.Key.Space && VideoPlayer.Visibility == Visibility.Visible)
            {
                if (VideoPlayer.CanPause)
                {
                    if (VideoPlayer.Position == VideoPlayer.NaturalDuration)
                        VideoPlayer.Play();
                    else
                        VideoPlayer.Pause();
                }
                e.Handled = true;
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // Stop video to release resources
            if (VideoPlayer.Source != null)
            {
                VideoPlayer.Stop();
                VideoPlayer.Source = null;
            }
            base.OnClosed(e);
        }
    }
}