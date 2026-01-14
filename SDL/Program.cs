using Microsoft.AspNetCore.Hosting;
using MudBlazor.Services;
using SDL.Components;
using SDL.Configuration;
using SDL.Services;
using SDL.Services.Downloading;
using SDL.Services.Conversion;
using SDL.Services.Archiving;
using SDL.Services.Infrastructure;
using SDL.Services.Queues;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseStaticWebAssets();

// Add MudBlazor services
builder.Services.AddMudServices();

// Configure VideoStorage settings
builder.Services.Configure<VideoStorageSettings>(
    builder.Configuration.GetSection("VideoStorage"));

// Add application services
builder.Services.AddSingleton<IFileSystemService, PhysicalFileSystemService>();
builder.Services.AddSingleton<IUrlService, UrlService>();
builder.Services.AddSingleton<VideoDatabaseService>();

// Register task queues
builder.Services.AddSingleton<IDownloadQueue, DownloadQueue>();
builder.Services.AddSingleton<IConversionQueue, ConversionQueue>();
builder.Services.AddSingleton<IArchiveQueue, ArchiveQueue>();

builder.Services.AddSingleton<VideoConversionService>();
builder.Services.AddSingleton<StreamDownloadService>();
builder.Services.AddSingleton<VideoArchiveService>();
builder.Services.AddSingleton<VideoManagementService>();

// Register background workers
builder.Services.AddHostedService<DownloadBackgroundService>();
builder.Services.AddHostedService<ConversionBackgroundService>();
builder.Services.AddHostedService<ArchiveBackgroundService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();