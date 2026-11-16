using ManeFfmpeg.Core.Platforms;

namespace ManeFfmpeg.Core.Notifications;

/// <summary>Desktop notifications without native bindings (<see cref="IPlatform.NotifyAsync"/>). Best-effort.</summary>
public static class SystemNotifier
{
    public static Task NotifyAsync(string title, string body) => Platform.Current.NotifyAsync(title, body);
}
