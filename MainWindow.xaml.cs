using Extension.Exif;
using Extension.Utilities;
using Microsoft.Extensions.DependencyInjection;
using PhotoGallery.Data;
using PhotoGallery.Model;
using PhotoGallery.Workers;
using Serilog;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace PhotoGallery
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<ThumbnailItem> Thumbnails { get; } = new();

        private ThumbnailFetchWorker? _fetchWorker;
        private readonly IPhotoRepository _repo;
        private int _currentColumns = 5;
        private const double CellMargin = 10;
        private string _currentFolderPath = string.Empty;
        private CancellationTokenSource? _scanCancellation;
        private ExifInfo? _currentSelectedExif;
        private string _currentSelectedFile = string.Empty;

        // Test folder path - update this to your actual photo folder
        private readonly string _defaultTestPath = @"D:\test2";

        public MainWindow()
            : this(App.Services.GetRequiredService<IPhotoRepository>())
        {
        }

        public MainWindow(IPhotoRepository repo)
        {
            InitializeComponent();
            DataContext = this;

            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _fetchWorker = new ThumbnailFetchWorker(_repo, Dispatcher, Thumbnails);

            InitializeFolderTree();
            ClearMetadataDisplay();

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;
            SizeChanged += (_, __) => RefreshLazyImageWidths();
            ColumnSelector.SelectionChanged += ColumnSelector_SelectionChanged;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            RefreshLazyImageWidths();

            // Load default folder if it exists
            if (Directory.Exists(_defaultTestPath))
            {
                LoadImagesFromFolder(_defaultTestPath);
                StartMetadataScan(_defaultTestPath);
            }
        }

        private void MainWindow_Closed(object sender, EventArgs e)
        {
            _fetchWorker?.Dispose();
            _scanCancellation?.Cancel();
            _scanCancellation?.Dispose();
        }

        #region Metadata Display

        private void ThumbnailList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThumbnailList.SelectedItem is ThumbnailItem selectedItem)
            {
                LoadMetadataForFile(selectedItem.OriginalPath);
            }
            else
            {
                ClearMetadataDisplay();
            }
        }

        private async void LoadMetadataForFile(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ClearMetadataDisplay();
                return;
            }

            try
            {
                _currentSelectedFile = filePath;
                StatusText.Text = "Loading metadata...";

                // Update selected file indicator
                SelectedFileText.Text = $"Selected: {Path.GetFileName(filePath)}";

                // Load EXIF data asynchronously
                _currentSelectedExif = await Task.Run(() => ExifReader.TryRead(filePath));

                if (_currentSelectedExif != null)
                {
                    DisplayMetadata(_currentSelectedExif, filePath);
                    StatusText.Text = "Metadata loaded";
                }
                else
                {
                    DisplayFileInfoOnly(filePath);
                    StatusText.Text = "No metadata available";
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load metadata for: {FilePath}", filePath);
                DisplayFileInfoOnly(filePath);
                StatusText.Text = "Error loading metadata";
            }
        }

        private void DisplayMetadata(ExifInfo exif, string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);

                // File Information
                FilenameValue.Text = fileInfo.Name;
                FileSizeValue.Text = FormatFileSize(fileInfo.Length);
                FilePathValue.Text = filePath;
                ModifiedDateValue.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

                // Media Information
                DateTakenValue.Text = exif.CreationDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                DateSourceValue.Text = exif.CreationDateSource.ToString();
                OrientationValue.Text = FormatOrientation(exif.Orientation);

                // Camera Information
                CameraMakeValue.Text = string.IsNullOrEmpty(exif.Make) ? "Unknown" : exif.Make;
                CameraModelValue.Text = string.IsNullOrEmpty(exif.Model) ? "Unknown" : exif.Model;

                // Camera Settings from EXIF tags
                DisplayCameraSettings(exif);

                // GPS Information
                DisplayGPSInformation(exif);

                // Tags and Keywords
                KeywordsValue.Text = exif.Keywords?.Length > 0 ? string.Join(", ", exif.Keywords) : "None";

                // Get tags from database
                var photoMeta = _repo.Get(filePath);
                TagsValue.Text = !string.IsNullOrEmpty(photoMeta?.TagsCsv) ? photoMeta.TagsCsv : "None";

                // Additional Information
                SoftwareValue.Text = exif.TryGet("Software") ?? "Unknown";
                ColorSpaceValue.Text = FormatColorSpace(exif.TryGet("ColorSpace"));
                CopyrightValue.Text = exif.TryGet("Copyright") ?? "None";

                // Try to get image dimensions
                DisplayImageDimensions(exif, filePath);

                // Raw EXIF data
                DisplayRawExifData(exif);

                // Enable action buttons
                OpenFileButton.IsEnabled = true;
                ShowInExplorerButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error displaying metadata for: {FilePath}", filePath);
                DisplayFileInfoOnly(filePath);
            }
        }

        private void DisplayCameraSettings(ExifInfo exif)
        {
            // ISO
            var iso = exif.TryGetInt("ISOSpeedRatings");
            ISOValue.Text = iso?.ToString() ?? "Unknown";

            // Aperture (F-number)
            var aperture = exif.TryGetRational("ApertureValue");
            if (aperture.HasValue)
            {
                ApertureValue.Text = $"f/{Math.Pow(2, aperture.Value / 2):F1}";
            }
            else
            {
                var fNumber = exif.TryGetRational("FocalLength");
                ApertureValue.Text = fNumber?.ToString("F1") ?? "Unknown";
            }

            // Shutter Speed
            var shutterSpeed = exif.TryGetRational("ShutterSpeedValue");
            if (shutterSpeed.HasValue)
            {
                ShutterSpeedValue.Text = $"1/{Math.Pow(2, shutterSpeed.Value):F0}s";
            }
            else
            {
                ShutterSpeedValue.Text = "Unknown";
            }

            // Focal Length
            var focalLength = exif.TryGetRational("FocalLength");
            FocalLengthValue.Text = focalLength?.ToString("F0") + "mm" ?? "Unknown";

            // Flash
            var flash = exif.TryGetInt("Flash");
            FlashValue.Text = FormatFlashMode(flash);

            // Lens information
            LensValue.Text = exif.TryGet("LensModel") ?? exif.TryGet("Lens") ?? "Unknown";
        }

        private void DisplayGPSInformation(ExifInfo exif)
        {
            var latitude = exif.TryGetGPS("GPSLatitude", "GPSLatitudeRef");
            var longitude = exif.TryGetGPS("GPSLongitude", "GPSLongitudeRef");

            if (latitude.HasValue && longitude.HasValue)
            {
                GPSValue.Text = $"{latitude.Value:F6}, {longitude.Value:F6}";
                OpenMapButton.Visibility = Visibility.Visible;
            }
            else
            {
                GPSValue.Text = "No GPS data";
                OpenMapButton.Visibility = Visibility.Collapsed;
            }
        }

        private void DisplayImageDimensions(ExifInfo exif, string filePath)
        {
            try
            {
                // Try to get dimensions from EXIF first
                var width = exif.TryGetInt("PixelXDimension") ?? exif.TryGetInt("ImageWidth");
                var height = exif.TryGetInt("PixelYDimension") ?? exif.TryGetInt("ImageHeight");

                if (width.HasValue && height.HasValue)
                {
                    DimensionsValue.Text = $"{width}×{height} pixels";
                }
                else if (Helper.KnownVideoExtensions.Contains(Path.GetExtension(filePath)))
                {
                    DimensionsValue.Text = "Video file";
                }
                else
                {
                    // Fallback: load image to get dimensions
                    using var img = System.Drawing.Image.FromFile(filePath);
                    DimensionsValue.Text = $"{img.Width}×{img.Height} pixels";
                }
            }
            catch
            {
                DimensionsValue.Text = "Unknown";
            }
        }

        private void DisplayRawExifData(ExifInfo exif)
        {
            var rawData = new System.Text.StringBuilder();

            foreach (var kvp in exif.AllTags.OrderBy(x => x.Key))
            {
                rawData.AppendLine($"{kvp.Key}: {kvp.Value}");
            }

            RawExifValue.Text = rawData.Length > 0 ? rawData.ToString() : "No EXIF data available";
        }

        private void DisplayFileInfoOnly(string filePath)
        {
            try
            {
                var fileInfo = new FileInfo(filePath);

                // Clear all fields first
                ClearMetadataFields();

                // Set basic file info
                FilenameValue.Text = fileInfo.Name;
                FileSizeValue.Text = FormatFileSize(fileInfo.Length);
                FilePathValue.Text = filePath;
                ModifiedDateValue.Text = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

                // Set defaults for other fields
                DateTakenValue.Text = "Unknown";
                DateSourceValue.Text = "File system";
                DimensionsValue.Text = "Unknown";

                // Enable action buttons
                OpenFileButton.IsEnabled = true;
                ShowInExplorerButton.IsEnabled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error displaying file info for: {FilePath}", filePath);
                ClearMetadataDisplay();
            }
        }

        private void ClearMetadataDisplay()
        {
            _currentSelectedExif = null;
            _currentSelectedFile = string.Empty;
            SelectedFileText.Text = "No image selected";
            ClearMetadataFields();
            OpenFileButton.IsEnabled = false;
            ShowInExplorerButton.IsEnabled = false;
            OpenMapButton.Visibility = Visibility.Collapsed;
        }

        private void ClearMetadataFields()
        {
            // File Information
            FilenameValue.Text = "";
            FileSizeValue.Text = "";
            FilePathValue.Text = "";
            ModifiedDateValue.Text = "";

            // Media Information
            DateTakenValue.Text = "";
            DateSourceValue.Text = "";
            DimensionsValue.Text = "";
            OrientationValue.Text = "";

            // Camera Information
            CameraMakeValue.Text = "";
            CameraModelValue.Text = "";
            LensValue.Text = "";

            // Camera Settings
            ISOValue.Text = "";
            ApertureValue.Text = "";
            ShutterSpeedValue.Text = "";
            FocalLengthValue.Text = "";
            FlashValue.Text = "";

            // GPS Information
            GPSValue.Text = "";

            // Tags
            KeywordsValue.Text = "";
            TagsValue.Text = "";

            // Additional Information
            SoftwareValue.Text = "";
            ColorSpaceValue.Text = "";
            CopyrightValue.Text = "";

            // Raw EXIF
            RawExifValue.Text = "";
        }

        #endregion

        #region Formatting Helpers

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
            return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
        }

        private static string FormatOrientation(int orientation)
        {
            return orientation switch
            {
                1 => "Normal",
                2 => "Mirrored horizontally",
                3 => "Rotated 180°",
                4 => "Mirrored vertically",
                5 => "Mirrored horizontally, rotated 90° CCW",
                6 => "Rotated 90° CW",
                7 => "Mirrored horizontally, rotated 90° CW",
                8 => "Rotated 90° CCW",
                _ => $"Unknown ({orientation})"
            };
        }

        private static string FormatFlashMode(int? flash)
        {
            if (!flash.HasValue) return "Unknown";

            return flash.Value switch
            {
                0 => "No flash",
                1 => "Flash fired",
                5 => "Flash fired, no return detected",
                7 => "Flash fired, return detected",
                9 => "Flash fired, compulsory mode",
                13 => "Flash fired, compulsory mode, no return detected",
                15 => "Flash fired, compulsory mode, return detected",
                16 => "No flash, compulsory mode",
                24 => "No flash, auto mode",
                25 => "Flash fired, auto mode",
                29 => "Flash fired, auto mode, no return detected",
                31 => "Flash fired, auto mode, return detected",
                32 => "No flash available",
                _ => $"Unknown ({flash.Value})"
            };
        }

        private static string FormatColorSpace(string? colorSpace)
        {
            if (string.IsNullOrEmpty(colorSpace)) return "Unknown";

            return colorSpace switch
            {
                "1" => "sRGB",
                "2" => "Adobe RGB",
                "65535" => "Uncalibrated",
                _ => colorSpace
            };
        }

        #endregion

        #region Button Event Handlers

        private void OpenFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentSelectedFile) && File.Exists(_currentSelectedFile))
            {
                try
                {
                    var preview = new ImagePreviewWindow(_currentSelectedFile);
                    preview.ShowDialog();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to open file: {FilePath}", _currentSelectedFile);
                    MessageBox.Show(this, $"Failed to open file:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void ShowInExplorerButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentSelectedFile) && File.Exists(_currentSelectedFile))
            {
                try
                {
                    Process.Start("explorer.exe", $"/select,\"{_currentSelectedFile}\"");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to open explorer for: {FilePath}", _currentSelectedFile);
                    MessageBox.Show(this, $"Failed to open file in Explorer:\n{ex.Message}", "Error",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void OpenMapButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSelectedExif != null)
            {
                var latitude = _currentSelectedExif.TryGetGPS("GPSLatitude", "GPSLatitudeRef");
                var longitude = _currentSelectedExif.TryGetGPS("GPSLongitude", "GPSLongitudeRef");

                if (latitude.HasValue && longitude.HasValue)
                {
                    try
                    {
                        var url = $"https://www.google.com/maps?q={latitude.Value:F6},{longitude.Value:F6}";
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = url,
                            UseShellExecute = true
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to open map for coordinates: {Lat}, {Lng}", latitude.Value, longitude.Value);
                        MessageBox.Show(this, $"Failed to open map:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        #endregion

        #region Existing Methods (preserved)

        private void InitializeFolderTree()
        {
            try
            {
                // Add common root folders
                var roots = new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    _defaultTestPath
                }.Where(Directory.Exists).Distinct();

                foreach (var root in roots)
                {
                    var rootItem = new TreeViewItem
                    {
                        Header = Path.GetFileName(root) ?? root,
                        Tag = root
                    };
                    rootItem.Expanded += Folder_Expanded;
                    rootItem.Selected += FolderTree_SelectedItemChanged;
                    rootItem.Items.Add(null); // Placeholder for lazy loading
                    FolderTree.Items.Add(rootItem);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize folder tree");
            }
        }

        private void StartMetadataScan(string folder)
        {
            if (MetadataScanner.IsScanning)
            {
                Log.Information("Metadata scan already in progress, skipping new scan");
                return;
            }

            _scanCancellation?.Cancel();
            _scanCancellation = new CancellationTokenSource();

            // Create progress handler
            var progress = new Progress<ScanProgress>(scanProgress =>
            {
                Dispatcher.Invoke(() =>
                {
                    StatusText.Text = scanProgress.IsComplete
                        ? "Scan complete"
                        : $"Scanning: {scanProgress.ProgressPercentage:F0}% ({scanProgress.ProcessedCount}/{scanProgress.TotalCount})";

                    Title = scanProgress.IsComplete
                        ? "Photo Gallery"
                        : $"Photo Gallery - Scanning: {scanProgress.ProgressPercentage:F0}%";
                });
            });

            // Run scan in background
            Task.Run(async () =>
            {
                try
                {
                    await MetadataScanner.RunParallelMetadataExtractionAsync(
                        folder,
                        maxConcurrency: Environment.ProcessorCount,
                        progress: progress,
                        cancellationToken: _scanCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    Log.Information("Metadata scan was cancelled");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Metadata scan failed for folder: {Folder}", folder);
                    Dispatcher.Invoke(() =>
                        MessageBox.Show(this, $"Metadata scan failed:\n{ex.Message}", "Error",
                            MessageBoxButton.OK, MessageBoxImage.Error));
                }
                finally
                {
                    Dispatcher.Invoke(() =>
                    {
                        Title = "Photo Gallery";
                        StatusText.Text = "Ready";
                    });
                }
            }, _scanCancellation.Token);
        }

        private void FolderTree_SelectedItemChanged(object sender, RoutedEventArgs e)
        {
            if (sender is TreeViewItem selectedItem && selectedItem.Tag is string path)
            {
                LoadImagesFromFolder(path);
                StartMetadataScan(path);
            }
        }

        private void Folder_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is not TreeViewItem item ||
                item.Items.Count != 1 ||
                item.Items[0] != null ||
                item.Tag is not string folderPath)
                return;

            item.Items.Clear();

            try
            {
                var dir = new DirectoryInfo(folderPath);
                var subDirs = dir.GetDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.Hidden | FileAttributes.System))
                    .OrderBy(d => d.Name);

                foreach (var subDir in subDirs)
                {
                    var subItem = new TreeViewItem
                    {
                        Header = subDir.Name,
                        Tag = subDir.FullName
                    };
                    subItem.Expanded += Folder_Expanded;
                    subItem.Selected += FolderTree_SelectedItemChanged;

                    // Add placeholder if directory has subdirectories
                    if (subDir.GetDirectories().Any())
                        subItem.Items.Add(null);

                    item.Items.Add(subItem);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to expand folder: {Path}", folderPath);
            }
        }

        private void LoadImagesFromFolder(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                return;

            _currentFolderPath = folderPath;
            Log.Information("Loading images from folder: {Folder}", folderPath);

            try
            {
                // Clear selection and metadata when changing folders
                ThumbnailList.SelectedItem = null;
                ClearMetadataDisplay();

                // Stop current fetch worker
                _fetchWorker?.Stop();

                // Start new worker for this folder
                _fetchWorker = new ThumbnailFetchWorker(_repo, Dispatcher, Thumbnails);
                _fetchWorker.Start(folderPath);

                StatusText.Text = $"Loading folder: {Path.GetFileName(folderPath)}";
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to load images from folder: {Folder}", folderPath);
                MessageBox.Show(this, $"Failed to load folder:\n{ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ReloadThumbnails_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_currentFolderPath))
            {
                LoadImagesFromFolder(_currentFolderPath);
            }
            else if (Directory.Exists(_defaultTestPath))
            {
                LoadImagesFromFolder(_defaultTestPath);
            }
            else
            {
                MessageBox.Show(this, "No folder selected. Please select a folder from the tree first.",
                    "Information", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void ColumnSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ColumnSelector.SelectedItem is ComboBoxItem cbi &&
                int.TryParse(cbi.Content?.ToString(), out int cols) &&
                cols > 0)
            {
                _currentColumns = cols;
                var grid = FindVisualChild<UniformGrid>(ThumbnailList);
                if (grid != null)
                    grid.Columns = cols;

                RefreshLazyImageWidths();
            }
        }

        private void RefreshLazyImageWidths()
        {
            if (_currentColumns <= 0 || ThumbnailList == null) return;

            double usableWidth = ThumbnailList.ActualWidth;
            if (usableWidth <= 0 || double.IsNaN(usableWidth)) return;

            double cellWidth = (usableWidth / _currentColumns) - CellMargin;
            if (cellWidth < 60) cellWidth = 60;

            foreach (var lazyImage in FindVisualChildren<Controls.LazyImage>(ThumbnailList))
                lazyImage.Width = cellWidth;
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) return t;

                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent)
            where T : DependencyObject
        {
            if (parent == null) yield break;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) yield return typed;

                foreach (var descendant in FindVisualChildren<T>(child))
                    yield return descendant;
            }
        }

        #endregion
    }
}