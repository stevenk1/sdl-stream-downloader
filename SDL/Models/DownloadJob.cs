using LiteDB;

namespace SDL.Models;

public class DownloadJob
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = string.Empty;
    public string Title { get; set; } = "Unknown";
    public string Resolution { get; set; } = "Best";
    public DownloadStatus Status { get; set; } = DownloadStatus.Starting;
    public double Progress { get; set; }
    public string Speed { get; set; } = string.Empty;
    public string Eta { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string? ConvertedFilePath { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string? ErrorMessage { get; set; }
    public string? Thumbnail { get; set; }
    public List<string> Thumbnails { get; set; } = new();
}

public enum DownloadStatus
{
    Starting,
    Downloading,
    Processing,
    Completed,
    Failed,
    Stopped,
    Converting,
    ConversionCompleted,
    ConversionFailed
}
