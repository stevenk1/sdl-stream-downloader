using LiteDB;
using Microsoft.Extensions.Options;
using SDL.Configuration;
using SDL.Models;
using System.Text.Json;

namespace SDL.Services;

public class VideoDatabaseService : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILogger<VideoDatabaseService> _logger;
    private readonly VideoStorageSettings _settings;
    private readonly IFileSystemService _fileSystem;

    private readonly ILiteCollection<DownloadJob> _downloads;
    private readonly ILiteCollection<ConversionJob> _conversions;
    private readonly ILiteCollection<ArchivedVideo> _archives;

    public VideoDatabaseService(
        IOptions<VideoStorageSettings> settings,
        ILogger<VideoDatabaseService> logger,
        IFileSystemService fileSystem)
    {
        _settings = settings.Value;
        _logger = logger;
        _fileSystem = fileSystem;

        // Create database file in the archive directory
        var dbPath = _fileSystem.CombinePaths(_settings.ArchiveDirectory, _settings.DatabaseFileName);
        _fileSystem.CreateDirectory(_settings.ArchiveDirectory);

        _db = new LiteDatabase(dbPath);

        _downloads = _db.GetCollection<DownloadJob>("downloads");
        _conversions = _db.GetCollection<ConversionJob>("conversions");
        _archives = _db.GetCollection<ArchivedVideo>("archives");

        // Create indexes for common queries
        _downloads.EnsureIndex(x => x.Status);
        _downloads.EnsureIndex(x => x.UpdatedAt);
        _conversions.EnsureIndex(x => x.Status);
        _conversions.EnsureIndex(x => x.DownloadJobId);
        _archives.EnsureIndex(x => x.ArchivedAt);
        _archives.EnsureIndex(x => x.Title);

        // Migrate existing JSON data on first run
        MigrateFromJson();
    }

    private void MigrateFromJson()
    {
        try
        {
            // Check if we already have data
            if (_archives.Count() > 0)
            {
                _logger.LogInformation("Database already contains data, skipping migration");
                return;
            }

            // Try to load from old JSON file
            var metadataPath = _fileSystem.CombinePaths(_settings.ArchiveDirectory, _settings.MetadataFile);
            if (_fileSystem.FileExists(metadataPath))
            {
                _logger.LogInformation("Migrating archived videos from JSON to LiteDB...");
                var json = _fileSystem.FileReadAllText(metadataPath);
                var videos = System.Text.Json.JsonSerializer.Deserialize<List<ArchivedVideo>>(json);

                if (videos != null && videos.Any())
                {
                    // Verify files still exist
                    var existingVideos = videos.Where(v => _fileSystem.FileExists(v.FilePath)).ToList();

                    foreach (var video in existingVideos)
                    {
                        _archives.Insert(video);
                    }

                    _logger.LogInformation("Migrated {Count} archived videos to database", existingVideos.Count);

                    // Optionally backup the JSON file
                    var backupPath = metadataPath + ".backup";
                    _fileSystem.FileMove(metadataPath, backupPath);
                    _logger.LogInformation("Backed up JSON metadata to {BackupPath}", backupPath);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating from JSON");
        }
    }

    // Download Job methods
    public DownloadJob? GetDownloadJob(string id) => _downloads.FindById(id);

    public IEnumerable<DownloadJob> GetAllDownloadJobs() => _downloads.FindAll();

    public IEnumerable<DownloadJob> GetActiveDownloadJobs()
    {
        return _downloads.Find(x =>
            x.Status != DownloadStatus.ConversionCompleted &&
            x.Status != DownloadStatus.ConversionFailed);
    }

    public IEnumerable<DownloadJob> GetConvertedJobs()
    {
        return _downloads.Find(x =>
            x.Status == DownloadStatus.ConversionCompleted ||
            x.Status == DownloadStatus.ConversionFailed);
    }

    public void UpsertDownloadJob(DownloadJob job)
    {
        job.UpdatedAt = DateTime.Now;
        _downloads.Upsert(job);
    }

    public bool DeleteDownloadJob(string id) => _downloads.Delete(id);

    // Conversion Job methods
    public ConversionJob? GetConversionJob(string id) => _conversions.FindById(id);

    public IEnumerable<ConversionJob> GetAllConversionJobs() => _conversions.FindAll();

    public IEnumerable<ConversionJob> GetActiveConversionJobs()
    {
        return _conversions.Find(x =>
            x.Status == ConversionStatus.Queued ||
            x.Status == ConversionStatus.Converting);
    }

    public ConversionJob? GetConversionByDownloadJobId(string downloadJobId)
    {
        return _conversions.FindOne(x => x.DownloadJobId == downloadJobId);
    }

    public void UpsertConversionJob(ConversionJob job)
    {
        job.UpdatedAt = DateTime.Now;
        _conversions.Upsert(job);
    }

    public bool DeleteConversionJob(string id) => _conversions.Delete(id);

    // Archived Video methods
    public ArchivedVideo? GetArchivedVideo(string id) => _archives.FindById(id);

    public IEnumerable<ArchivedVideo> GetAllArchivedVideos() => _archives.FindAll();

    public void UpsertArchivedVideo(ArchivedVideo video) => _archives.Upsert(video);

    public bool DeleteArchivedVideo(string id) => _archives.Delete(id);

    public (IEnumerable<ArchivedVideo> Items, int TotalCount) GetArchivedVideosFiltered(int skip, int limit, string? sortBy, bool descending, string? search)
    {
        var query = _archives.Query();

        if (!string.IsNullOrWhiteSpace(search))
        {
            // LiteDB doesn't support StringComparison in expressions easily, so we use lowercase if needed or just simple contains
            // For now, let's keep it simple.
            query = query.Where(x => x.Title.Contains(search) || x.OriginalUrl.Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(sortBy))
        {
            if (descending)
                query = query.OrderByDescending(BsonExpression.Create(sortBy));
            else
                query = query.OrderBy(BsonExpression.Create(sortBy));
        }
        else
        {
            query = query.OrderByDescending(x => x.ArchivedAt);
        }

        var totalCount = query.Count();
        var items = query.Skip(skip).Limit(limit).ToList();

        return (items, totalCount);
    }

    public int GetArchivedVideoCount() => _archives.Count();

    // Transaction support
    public void BeginTransaction() => _db.BeginTrans();
    public void CommitTransaction() => _db.Commit();
    public void RollbackTransaction() => _db.Rollback();

    public void Dispose()
    {
        _db?.Dispose();
    }
}
