namespace SDL.Services.Infrastructure;

public interface IStreamCheckService
{
    Task<bool> IsLiveAsync(string url);
}
