using LiteDB;

namespace SDL.Models;

public class ConversionJob
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string SourcePath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string Title { get; set; } = "Unknown";
    public string OriginalUrl { get; set; } = string.Empty;
    public ConversionStatus Status { get; set; } = ConversionStatus.Queued;
    public double Progress { get; set; }
    public string Speed { get; set; } = string.Empty;
    public string Fps { get; set; } = string.Empty;
    public string Eta { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string? ErrorMessage { get; set; }
    public string DownloadJobId { get; set; } = string.Empty;
}

public enum ConversionStatus
{
    Queued,
    Converting,
    Completed,
    Failed
}
