namespace SDL.Models;

public class ArchivedVideo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Title { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public string? Duration { get; set; }
    public string? Thumbnail { get; set; }
    public List<string> Thumbnails { get; set; } = new();
    public DateTime ArchivedAt { get; set; } = DateTime.Now;
    public string? Description { get; set; }
    public string? Uploader { get; set; }

    public string FileSizeFormatted => FormatFileSize(FileSizeBytes);

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}
