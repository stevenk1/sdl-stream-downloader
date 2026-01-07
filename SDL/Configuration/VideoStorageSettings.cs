namespace SDL.Configuration;

public class VideoStorageSettings
{
    public string DownloadDirectory { get; set; } = "Downloads";
    public string ConvertedDirectory { get; set; } = "Converted";
    public string ArchiveDirectory { get; set; } = "Archives";
    public string ThumbnailDirectory { get; set; } = "Thumbnails";
    public string MetadataFile { get; set; } = "video-metadata.json";
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string OutputFormat { get; set; } = "mp4";

    // FFmpeg conversion settings
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string ConversionOutputFormat { get; set; } = "webm";
    public string VideoCodec { get; set; } = "libsvtav1";
    public string AudioCodec { get; set; } = "libopus";
    public int VideoCrf { get; set; } = 30;
    public string AudioBitrate { get; set; } = "128k";
    public string VideoPreset { get; set; } = "8"; // AV1 preset: 0-13, higher is faster (but lower quality)

    // VP9-specific settings
    public string? Vp9Quality { get; set; } = null; // e.g., "realtime", "good", "best"
    public int? Vp9Speed { get; set; } = null; // 0-5 for realtime, 0-4 for good quality
    public int? Vp9RowMt { get; set; } = null; // Row-based multithreading: 0 or 1
    public int? Vp9TileColumns { get; set; } = null; // Number of tile columns for parallel encoding
}
