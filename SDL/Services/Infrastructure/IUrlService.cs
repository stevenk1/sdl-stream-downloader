using SDL.Models;

namespace SDL.Services.Infrastructure;

public interface IUrlService
{
    string GetThumbnailUrl(string? thumbnailFileName);
    string GetVideoUrl(ArchivedVideo? video);
    string GetDownloadVideoUrl(string? fileName);
    string GetConvertedVideoUrl(string? fileName);
    string GetArchiveVideoUrl(string? fileName);
}
