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
        if (video == null)
            return string.Empty;

        var fileName = video.FileName;

        // If the file path contains "converted", serve from converted directory
        // This is primarily for temporary ArchivedVideo objects created for playback of non-archived videos
        if (video.FilePath.Contains(_settings.ConvertedDirectory, StringComparison.OrdinalIgnoreCase) || 
            video.FilePath.Contains("converted", StringComparison.OrdinalIgnoreCase))
        {
            return GetConvertedVideoUrl(fileName);
        }

        return GetArchiveVideoUrl(fileName);
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
