using Extension.Exif;
using Microsoft.Data.Sqlite;
using PhotoGallery.Data;
using Serilog;
using System.Data;
using System.IO;

namespace PhotoGallery
{
    public static class PhotoDb
    {
        private static readonly string DbPath =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "PhotoGallery", "PhotoGallery.db");

        private static readonly string CxnString =
            $"Data Source={DbPath};Foreign Keys=True";

        private static bool _initialized;
        private static readonly object _initLock = new();

        private static readonly ILogger _logger;
        private const int MaxRetries = 3;
        private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

        static PhotoDb()
        {
            var logDir = Path.GetDirectoryName(DbPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logPath = Path.Combine(logDir, "PhotoGallery.log");

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(logPath, rollingInterval: RollingInterval.Day)
                .CreateLogger();

            _logger = Log.Logger.ForContext(typeof(PhotoDb));
        }

        public static event Action<string>? PhotoCommitted;

        public static void Initialize()
        {
            ExecuteWithRetry(() =>
            {
                lock (_initLock)
                {
                    if (_initialized) return;
                    _logger.Information("Initializing PhotoDb at {Path}", DbPath);

                    var dir = Path.GetDirectoryName(DbPath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        _logger.Debug("Creating directory {Dir}", dir);
                        Directory.CreateDirectory(dir);
                    }

                    using var conn = new SqliteConnection(CxnString);
                    conn.Open();
                    EnablePragmas(conn);
                    CreateSchema(conn);
                    CreateIndexes(conn);
                    _initialized = true;
                    _logger.Information("PhotoDb initialized successfully");
                }
            });
        }

        public static bool Exists(string filePath)
        {
            return ExecuteWithRetry(() =>
            {
                ValidateFilePath(filePath);
                Initialize();
                _logger.Debug("Checking existence for {FilePath}", filePath);

                using var conn = new SqliteConnection(CxnString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT 1 FROM Photos WHERE FilePath = @path LIMIT 1;";
                cmd.Parameters.AddWithValue("@path", filePath);

                using var reader = cmd.ExecuteReader();
                bool exists = reader.Read();
                _logger.Debug("Exists result for {FilePath}: {Exists}", filePath, exists);
                return exists;
            });
        }

        public static void UpsertPhoto(ExifInfo exifInfo)
        {
            ExecuteWithRetry(() =>
            {
                ValidateExifInfo(exifInfo);
                Initialize();
                _logger.Information("Upserting photo {FilePath}", exifInfo.FullPath);

                using var conn = new SqliteConnection(CxnString);
                conn.Open();
                using var tx = conn.BeginTransaction();

                long? sourceId = GetOrCreateScalar(tx, "DataSources", "Id", "Name", exifInfo.Source);
                long? makeId = !string.IsNullOrWhiteSpace(exifInfo.Make)
                    ? GetOrCreateScalar(tx, "CameraMakes", "Id", "Name", exifInfo.Make.Trim()) : null;
                long? modelId = makeId.HasValue && !string.IsNullOrWhiteSpace(exifInfo.Model)
                    ? GetOrAddModel(tx, makeId.Value, exifInfo.Model.Trim()) : null;

                string tagCsv = exifInfo.Keywords != null
                    ? string.Join(';', exifInfo.Keywords.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct())
                    : string.Empty;

                using (var cmd = tx.Connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO Photos
  (FilePath, DateTaken, DataSourceId, Orientation, CameraMakeId, CameraModelId, Thumbnail, TagsCsv)
VALUES
  (@path,@date,@srcId,@orient,@makeId,@modelId,@thumb,@tags)
ON CONFLICT(FilePath) DO UPDATE SET
  DateTaken     = excluded.DateTaken,
  DataSourceId  = excluded.DataSourceId,
  Orientation   = excluded.Orientation,
  CameraMakeId  = excluded.CameraMakeId,
  CameraModelId = excluded.CameraModelId,
  Thumbnail     = excluded.Thumbnail,
  TagsCsv       = excluded.TagsCsv;";
                    cmd.Parameters.AddWithValue("@path", exifInfo.FullPath);
                    cmd.Parameters.AddWithValue("@date", new DateTimeOffset(exifInfo.CreationDateTime).ToUnixTimeSeconds());
                    cmd.Parameters.AddWithValue("@srcId", sourceId.HasValue ? sourceId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@orient", exifInfo.Orientation);
                    cmd.Parameters.AddWithValue("@makeId", makeId.HasValue ? makeId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@modelId", modelId.HasValue ? modelId.Value : DBNull.Value);
                    cmd.Parameters.AddWithValue("@thumb", (object?)exifInfo.ThumbnailBytes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@tags", tagCsv);
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                _logger.Information("Photo upserted: {FilePath}", exifInfo.FullPath);
                PhotoCommitted?.Invoke(exifInfo.FullPath);
            });
        }

        public static IEnumerable<PhotoMeta> GetAllUnder(string folderPath)
        {
            return ExecuteWithRetry(() =>
            {
                ValidateFolderPath(folderPath);
                Initialize();
                _logger.Debug("Retrieving all photos under {Folder}", folderPath);

                var list = new List<PhotoMeta>();
                using var conn = new SqliteConnection(CxnString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT p.FilePath,
       p.DateTaken,
       ds.Name AS DataSource,
       cm.Name AS CameraMake,
       mo.Name AS CameraModel,
       p.Orientation,
       p.TagsCsv,
       p.Thumbnail
FROM Photos p
LEFT JOIN DataSources ds ON ds.Id = p.DataSourceId
LEFT JOIN CameraMakes cm ON cm.Id = p.CameraMakeId
LEFT JOIN CameraModels mo ON mo.Id = p.CameraModelId
WHERE p.FilePath LIKE @prefix || '%';";
                cmd.Parameters.AddWithValue("@prefix", folderPath);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var file = reader.GetString(0);
                    var epoch = reader.IsDBNull(1) ? 0L : reader.GetInt64(1);
                    var source = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                    var make = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
                    var model = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                    var orient = reader.IsDBNull(5) ? 1 : reader.GetInt32(5);
                    var tags = reader.IsDBNull(6) ? string.Empty : reader.GetString(6);
                    var thumb = reader.IsDBNull(7) ? null : (byte[])reader[7];

                    list.Add(new PhotoMeta(
                        FilePath: file,
                        DateTakenEpoch: epoch,
                        DateSource: source,
                        CameraMake: make,
                        CameraModel: model,
                        Orientation: orient,
                        TagsCsv: tags,
                        ThumbnailBlob: thumb
                    ));
                }
                _logger.Information("Retrieved {Count} photos under {Folder}", list.Count, folderPath);
                return list;
            });
        }

        public static PhotoRecord? GetPhoto(string filePath)
        {
            return ExecuteWithRetry(() =>
            {
                ValidateFilePath(filePath);
                Initialize();
                _logger.Debug("Retrieving photo {FilePath}", filePath);

                using var conn = new SqliteConnection(CxnString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT p.FilePath, p.DateTaken, ds.Name, cm.Name, mo.Name,
       p.Orientation, p.Thumbnail, p.TagsCsv
FROM Photos p
LEFT JOIN DataSources ds ON ds.Id = p.DataSourceId
LEFT JOIN CameraMakes cm ON cm.Id = p.CameraMakeId
LEFT JOIN CameraModels mo ON mo.Id = p.CameraModelId
WHERE p.FilePath = @path;";
                cmd.Parameters.AddWithValue("@path", filePath);

                using var rdr = cmd.ExecuteReader();
                if (!rdr.Read())
                {
                    _logger.Warning("Photo not found: {FilePath}", filePath);
                    return null;
                }

                var record = new PhotoRecord
                {
                    FilePath = rdr.GetString(0),
                    DateTakenEpoch = rdr.IsDBNull(1) ? 0L : rdr.GetInt64(1),
                    DateSource = rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2),
                    CameraMake = rdr.IsDBNull(3) ? string.Empty : rdr.GetString(3),
                    CameraModel = rdr.IsDBNull(4) ? string.Empty : rdr.GetString(4),
                    Orientation = rdr.IsDBNull(5) ? 1 : rdr.GetInt32(5),
                    ThumbnailBlob = rdr.IsDBNull(6) ? null : (byte[])rdr[6],
                    TagsCsv = rdr.IsDBNull(7) ? string.Empty : rdr.GetString(7)
                };
                _logger.Information("Retrieved photo record for {FilePath}", filePath);
                return record;
            });
        }

        #region Internal Helpers
        private static void EnablePragmas(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
            cmd.ExecuteNonQuery();
        }

        private static void CreateSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS DataSources(
    Id   INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT UNIQUE
);
CREATE TABLE IF NOT EXISTS CameraMakes(
    Id   INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT UNIQUE
);
CREATE TABLE IF NOT EXISTS CameraModels(
    Id     INTEGER PRIMARY KEY AUTOINCREMENT,
    MakeId INTEGER,
    Name   TEXT,
    UNIQUE(MakeId, Name),
    FOREIGN KEY(MakeId) REFERENCES CameraMakes(Id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS Photos(
    FilePath      TEXT PRIMARY KEY,
    DateTaken     INTEGER,
    DataSourceId  INTEGER,
    Orientation   INT,
    CameraMakeId  INTEGER,
    CameraModelId INTEGER,
    Thumbnail     BLOB,
    TagsCsv       TEXT,
    FOREIGN KEY(DataSourceId)  REFERENCES DataSources(Id),
    FOREIGN KEY(CameraMakeId)  REFERENCES CameraMakes(Id),
    FOREIGN KEY(CameraModelId) REFERENCES CameraModels(Id)
);";
            cmd.ExecuteNonQuery();
        }

        private static void CreateIndexes(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE INDEX IF NOT EXISTS IX_Photos_DateTaken ON Photos(DateTaken);
CREATE INDEX IF NOT EXISTS IX_Photos_SourceId ON Photos(DataSourceId);";
            cmd.ExecuteNonQuery();
        }

        private static long GetOrCreateScalar(SqliteTransaction tx, string table, string keyCol, string uniqueCol, string uniqueValue)
        {
            using var ins = tx.Connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = $"INSERT OR IGNORE INTO {table}({uniqueCol}) VALUES(@val);";
            ins.Parameters.AddWithValue("@val", uniqueValue.Trim());
            ins.ExecuteNonQuery();

            using var sel = tx.Connection.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = $"SELECT {keyCol} FROM {table} WHERE {uniqueCol}=@val LIMIT 1;";
            sel.Parameters.AddWithValue("@val", uniqueValue.Trim());
            return Convert.ToInt64(sel.ExecuteScalar()!);
        }

        private static long GetOrAddModel(SqliteTransaction tx, long makeId, string modelName)
        {
            using var ins = tx.Connection.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR IGNORE INTO CameraModels(MakeId,Name) VALUES(@make,@name);";
            ins.Parameters.AddWithValue("@make", makeId);
            ins.Parameters.AddWithValue("@name", modelName.Trim());
            ins.ExecuteNonQuery();

            using var sel = tx.Connection.CreateCommand();
            sel.Transaction = tx;
            sel.CommandText = "SELECT Id FROM CameraModels WHERE MakeId=@make AND Name=@name LIMIT 1;";
            sel.Parameters.AddWithValue("@make", makeId);
            sel.Parameters.AddWithValue("@name", modelName.Trim());
            return Convert.ToInt64(sel.ExecuteScalar()!);
        }

        private static void ExecuteWithRetry(Action action)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    attempt++;
                    action();
                    return;
                }
                catch (SqliteException ex) when (attempt < MaxRetries)
                {
                    _logger.Warning(ex, "Attempt {Attempt} failed, retrying...", attempt);
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        private static T ExecuteWithRetry<T>(Func<T> func)
        {
            int attempt = 0;
            while (true)
            {
                try
                {
                    attempt++;
                    return func();
                }
                catch (SqliteException ex) when (attempt < MaxRetries)
                {
                    _logger.Warning(ex, "Attempt {Attempt} failed, retrying...", attempt);
                    Thread.Sleep(RetryDelay);
                }
            }
        }

        private static void ValidateFilePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.Error("Invalid file path: '{Path}'", path);
                throw new ArgumentException("File path cannot be null or empty.", nameof(path));
            }
        }

        private static void ValidateFolderPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _logger.Error("Invalid folder path: '{Path}'", path);
                throw new ArgumentException("Folder path cannot be null or empty.", nameof(path));
            }
        }

        private static void ValidateExifInfo(ExifInfo exif)
        {
            if (exif == null)
            {
                _logger.Error("ExifInfo is null");
                throw new ArgumentNullException(nameof(exif));
            }
            if (string.IsNullOrWhiteSpace(exif.FullPath))
            {
                _logger.Error("ExifInfo.FullPath is invalid: '{FullPath}'", exif.FullPath);
                throw new ArgumentException("ExifInfo.FullPath cannot be null or empty.", nameof(exif.FullPath));
            }
        }
        #endregion
    }

    public class PhotoRecord
    {
        public string FilePath { get; set; } = string.Empty;
        public long DateTakenEpoch { get; set; }
        public string DateSource { get; set; } = string.Empty;
        public string CameraMake { get; set; } = string.Empty;
        public string CameraModel { get; set; } = string.Empty;
        public int Orientation { get; set; }
        public byte[]? ThumbnailBlob { get; set; }
        public string TagsCsv { get; set; } = string.Empty;

        public DateTime DateTaken => DateTimeOffset.FromUnixTimeSeconds(DateTakenEpoch).DateTime;
    }
}
