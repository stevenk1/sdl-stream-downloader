# SDL - Stream Download & Archive

A web-based application for downloading, converting, and archiving video streams from platforms like Twitch, YouTube, and other yt-dlp supported sites.

## Features

- **Stream Downloading**: Download live and archived streams from YouTube, Twitch, and other platforms supported by yt-dlp
- **Quality Selection**: Choose from multiple quality options (Best, 1080p, 720p, 480p, 360p)
- **Automatic Conversion**: Downloaded videos are automatically converted using FFmpeg with configurable codecs and bitrates
- **Thumbnail Generation**: Automatically generates multiple thumbnails (6 per video) at different timestamps for easy preview
- **Video Archive**: Store and manage downloaded videos with metadata (title, URL, file size, duration)
- **Real-time Progress**: Live progress tracking for downloads and conversions with speed, ETA, and FPS metrics
- **Modern UI**: Clean, responsive web interface built with Blazor and MudBlazor
- **Video Playback**: Built-in video player for viewing archived videos
- **Thumbnail Gallery**: View and browse multiple thumbnails for each video

## Tech Stack

- **Backend**: ASP.NET Core 10.0 with Blazor Server
- **UI Framework**: MudBlazor
- **Video Download**: yt-dlp
- **Video Processing**: FFmpeg and ffprobe
- **Container**: Docker with docker-compose support

## Prerequisites

### For Local Development
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [yt-dlp](https://github.com/yt-dlp/yt-dlp) (command-line tool)
- [FFmpeg](https://ffmpeg.org/) (with ffprobe)

### For Docker Deployment
- Docker
- Docker Compose

## Installation & Setup

### Option 1: Running Locally

1. **Install Dependencies**

   Install yt-dlp:
   ```bash
   # macOS (via Homebrew)
   brew install yt-dlp

   # Linux
   sudo curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/bin/yt-dlp
   sudo chmod a+rx /usr/bin/yt-dlp

   # Windows (via pip)
   pip install yt-dlp
   ```

   Install FFmpeg:
   ```bash
   # macOS (via Homebrew)
   brew install ffmpeg

   # Ubuntu/Debian
   sudo apt update && sudo apt install ffmpeg

   # Windows: Download from https://ffmpeg.org/download.html
   ```

2. **Clone the Repository**
   ```bash
   git clone <repository-url>
   cd SDL
   ```

3. **Configure Application Settings** (Optional)

   Edit `SDL/appsettings.json` to customize storage paths and FFmpeg settings:
   ```json
   {
     "VideoStorage": {
       "DownloadDirectory": "wwwroot/downloads",
       "ConvertedDirectory": "wwwroot/converted",
       "ArchiveDirectory": "wwwroot/archives",
       "ThumbnailDirectory": "wwwroot/thumbnails",
       "YtDlpPath": "yt-dlp",
       "FfmpegPath": "ffmpeg",
       "OutputFormat": "mp4",
       "VideoCodec": "copy",
       "AudioCodec": "aac",
       "AudioBitrate": "128k"
     }
   }
   ```

4. **Run the Application**
   ```bash
   cd SDL
   dotnet run
   ```

5. **Access the Application**

   Open your browser and navigate to `http://localhost:5000` (or the port shown in the terminal)

### Option 2: Running with Docker

1. **Clone the Repository**
   ```bash
   git clone <repository-url>
   cd SDL
   ```

2. **Start with Docker Compose**
   ```bash
   docker-compose up -d
   ```

3. **Access the Application**

   Open your browser and navigate to `http://localhost:8347`

4. **Stop the Application**
   ```bash
   docker-compose down
   ```

## Usage

### Downloading a Stream

1. Navigate to the home page
2. Enter a stream URL in the input field (e.g., `https://www.youtube.com/watch?v=...` or `https://twitch.tv/videos/...`)
3. Select your desired quality from the dropdown menu
4. Click "Start Download"
5. Monitor the download progress in real-time

### Managing Downloads

- **Active Downloads**: View ongoing downloads with progress, speed, and ETA
- **Stop Download**: Cancel an in-progress download (partial files are preserved)
- **Active Conversions**: Automatic conversion starts after download completes
- **Converted Videos**: Play or archive successfully converted videos

### Working with Archived Videos

- **Browse Archive**: View all archived videos in a sortable, filterable table
- **Play Video**: Click the play button to watch videos in the built-in player
- **View Thumbnails**: Click on any thumbnail to view the full gallery of 6 thumbnails
- **Delete Video**: Remove videos from the archive (files are permanently deleted)

## Configuration

### VideoStorage Settings

| Setting | Description | Default |
|---------|-------------|---------|
| `DownloadDirectory` | Where downloaded files are stored | `wwwroot/downloads` |
| `ConvertedDirectory` | Where converted files are stored | `wwwroot/converted` |
| `ArchiveDirectory` | Where archived videos are stored | `wwwroot/archives` |
| `ThumbnailDirectory` | Where thumbnails are stored | `wwwroot/thumbnails` |
| `MetadataFile` | JSON file for video metadata | `video-metadata.json` |
| `YtDlpPath` | Path to yt-dlp executable | `yt-dlp` |
| `FfmpegPath` | Path to FFmpeg executable | `ffmpeg` |
| `OutputFormat` | Video output format | `mp4` |
| `VideoCodec` | Video codec for conversion | `copy` |
| `AudioCodec` | Audio codec for conversion | `aac` |
| `AudioBitrate` | Audio bitrate | `128k` |

## Docker Volumes

The Docker setup uses persistent volumes to store your data:

- `./data/downloads` → `/app/wwwroot/downloads` - Downloaded video files
- `./data/converted` → `/app/wwwroot/converted` - Converted video files
- `./data/archives` → `/app/wwwroot/archives` - Archived videos
- `./data/thumbnails` → `/app/wwwroot/thumbnails` - Video thumbnails
- `./data/metadata` → `/app/metadata` - Video metadata

## Project Structure

```
SDL/
├── SDL/
│   ├── Components/         # Blazor components and pages
│   │   ├── Pages/         # Main application pages
│   │   └── Dialogs/       # Modal dialogs (video player, thumbnail gallery)
│   ├── Services/          # Core services
│   │   ├── StreamDownloadService.cs    # Handles video downloads via yt-dlp
│   │   ├── VideoConversionService.cs   # Handles FFmpeg conversions
│   │   └── VideoArchiveService.cs      # Manages video archive
│   ├── Models/            # Data models (DownloadJob, ConversionJob, etc.)
│   ├── Configuration/     # Configuration classes
│   └── wwwroot/          # Static files and video storage
├── Dockerfile            # Docker container configuration
├── docker-compose.yml    # Docker Compose orchestration
└── README.md            # This file
```

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Contributing

[Specify contribution guidelines here]
