using MudBlazor.Services;
using SDL.Components;
using SDL.Configuration;
using SDL.Services;

var builder = WebApplication.CreateBuilder(args);

// Add MudBlazor services
builder.Services.AddMudServices();

// Configure VideoStorage settings
builder.Services.Configure<VideoStorageSettings>(
    builder.Configuration.GetSection("VideoStorage"));

// Add application services
builder.Services.AddSingleton<VideoConversionService>();
builder.Services.AddSingleton<StreamDownloadService>();
builder.Services.AddSingleton<VideoArchiveService>();

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