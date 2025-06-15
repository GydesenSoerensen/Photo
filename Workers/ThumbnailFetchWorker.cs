using PhotoGallery.Data;
using PhotoGallery.Model;
using Serilog;
using System.Collections.ObjectModel;
using System.Windows.Threading;

namespace PhotoGallery.Workers
{
    public class ThumbnailFetchWorker : IDisposable
    {
        private readonly IPhotoRepository _repo;
        private readonly Dispatcher _dispatcher;
        private readonly ObservableCollection<ThumbnailItem> _thumbnails;
        private readonly HashSet<string> _shownPaths = new(StringComparer.OrdinalIgnoreCase);

        private string? _folderPath;
        private bool _isRunning;

        public ThumbnailFetchWorker(
            IPhotoRepository repo,
            Dispatcher dispatcher,
            ObservableCollection<ThumbnailItem> thumbnails)
        {
            _repo = repo ?? throw new ArgumentNullException(nameof(repo));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        }

        /// <summary>
        /// Begins loading thumbnails for the given folder and subscribes to new commits.
        /// </summary>
        public void Start(string folderPath)
        {
            if (_isRunning)
                Stop();

            _folderPath = folderPath;
            _shownPaths.Clear();
            _thumbnails.Clear();

            // Load existing photos
            LoadExisting();

            // Delay thumbnail inflow until at least 5 are committed or 5 seconds pass
            int count = 0;
            var ready = new ManualResetEvent(false);

            void OnBatchCommit(string _)
            {
                if (Interlocked.Increment(ref count) >= 100)
                    ready.Set();
            }

            PhotoDb.PhotoCommitted += OnBatchCommit;
            _isRunning = true;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ready.WaitOne(TimeSpan.FromSeconds(5));
                }
                finally
                {
                    ready.Dispose();
                    PhotoDb.PhotoCommitted -= OnBatchCommit;
                    PhotoDb.PhotoCommitted += OnPhotoCommitted;
                }
            });
        }

        /// <summary>
        /// Stops the worker and unsubscribes from further photo commits.
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            PhotoDb.PhotoCommitted -= OnPhotoCommitted;
            _isRunning = false;
        }

        private void LoadExisting()
        {
            try
            {
                if (string.IsNullOrEmpty(_folderPath)) return;

                var metas = _repo
                    .GetAllUnder(_folderPath)
                    .Where(m => m.ThumbnailBlob?.Length > 0)
                    .ToList();

                var newItems = metas
                    .Where(m => !_shownPaths.Contains(m.FilePath))
                    .Select(m => new ThumbnailItem
                    {
                        OriginalPath = m.FilePath,
                        Thumbnail = ThumbnailItem.LoadBitmap(m.ThumbnailBlob!)
                    })
                    .ToList();

                foreach (var item in newItems)
                    _shownPaths.Add(item.OriginalPath);

                _dispatcher.Invoke(() =>
                {
                    foreach (var item in newItems)
                        _thumbnails.Add(item);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ThumbnailFetchWorker] Failed loading existing thumbnails");
            }
        }


        private void OnPhotoCommitted(string fullPath)
        {
            try
            {
                if (!_isRunning || string.IsNullOrEmpty(_folderPath))
                    return;

                // Only add if the new photo lives under our folder
                if (!fullPath.StartsWith(_folderPath, StringComparison.OrdinalIgnoreCase))
                    return;

                var meta = _repo.Get(fullPath);
                if (meta?.ThumbnailBlob?.Length > 0)
                    AddIfNew(fullPath, meta.ThumbnailBlob);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ThumbnailFetchWorker] Error handling PhotoCommitted for {Path}", fullPath);
            }
        }

        private void AddIfNew(string path, byte[] blob)
        {
            if (_shownPaths.Contains(path)) return;

            _dispatcher.Invoke(() =>
            {
                var item = new ThumbnailItem
                {
                    OriginalPath = path,
                    Thumbnail = ThumbnailItem.LoadBitmap(blob)
                };
                _thumbnails.Add(item);
                _shownPaths.Add(path);
            });
        }

        public void Dispose() => Stop();
    }
}
