using LiteDB;

namespace SDL.Models;

public class Subscription
{
    [BsonId]
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Url { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CheckRateMinutes { get; set; } = 30;
    public string Resolution { get; set; } = "Best";
    public DateTime? LastCheckedAt { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
