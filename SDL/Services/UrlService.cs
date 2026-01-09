using Microsoft.Extensions.Options;
using SDL.Configuration;
using SDL.Models;

namespace SDL.Services;

public class UrlService : IUrlService
{
    private readonly VideoStorageSettings _settings;

    public UrlService(IOptions<VideoStorageSettings> settings)
    {
        _settings = settings.Value;
    }

    public string GetThumbnailUrl(string? thumbnailFileName)
    {
        if (string.IsNullOrEmpty(thumbnailFileName))
            return string.Empty;

        return $"{_settings.ThumbnailUrlPrefix}{thumbnailFileName}";
    }

    public string GetVideoUrl(ArchivedVideo? video)
    {
        if (video == null || string.IsNullOrEmpty(video.FilePath))
            return string.Empty;

        var fileName = video.FileName;
        var filePath = video.FilePath;

        // Determine which directory the file is in to use the correct URL prefix
        // We check for the directory name with separators to avoid false positives with filenames
        if (filePath.Contains(_settings.ConvertedDirectory, StringComparison.OrdinalIgnoreCase) || 
            filePath.Contains("/converted/", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("\\converted\\", StringComparison.OrdinalIgnoreCase))
        {
            return GetConvertedVideoUrl(fileName);
        }

        if (filePath.Contains(_settings.DownloadDirectory, StringComparison.OrdinalIgnoreCase) || 
            filePath.Contains("/downloads/", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("\\downloads\\", StringComparison.OrdinalIgnoreCase))
        {
            return GetDownloadVideoUrl(fileName);
        }

        // Default to archive if not specifically in converted or downloads
        return GetArchiveVideoUrl(fileName);
    }

    public string GetDownloadVideoUrl(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        return $"{_settings.DownloadUrlPrefix}{fileName}";
    }

    public string GetConvertedVideoUrl(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        return $"{_settings.ConvertedUrlPrefix}{fileName}";
    }

    public string GetArchiveVideoUrl(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        return $"{_settings.ArchiveUrlPrefix}{fileName}";
    }
}
