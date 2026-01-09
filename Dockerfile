# Base stage for runtime dependencies
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

# Install dependencies: ffmpeg, python3, yt-dlp, and deno
RUN apt-get update && apt-get install -y \
    ffmpeg \
    python3 \
    curl \
    ca-certificates \
    unzip \
    && curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/bin/yt-dlp \
    && chmod a+rx /usr/bin/yt-dlp \
    && curl -fsSL https://deno.land/install.sh | sh \
    && mv /root/.deno/bin/deno /usr/bin/deno \
    && rm -rf /root/.deno \
    && rm -rf /var/lib/apt/lists/*

# Build stage
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY SDL/SDL.csproj SDL/
RUN dotnet restore SDL/SDL.csproj

# Copy everything else and build
COPY SDL/ SDL/
WORKDIR /src/SDL
RUN dotnet build SDL.csproj -c Release -o /app/build

# Publish stage
FROM build AS publish
RUN dotnet publish SDL.csproj -c Release -o /app/publish /p:UseAppHost=false

# Runtime stage
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Create directories for video storage
RUN mkdir -p wwwroot/downloads wwwroot/converted wwwroot/archives wwwroot/thumbnails

# Run the application
ENTRYPOINT ["dotnet", "SDL.dll"]
