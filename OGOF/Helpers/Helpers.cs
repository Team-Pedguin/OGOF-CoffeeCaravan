namespace OGOF;

public static class Helpers
{
    public static void ApplyDefaults(this OgofConfig config)
    {
        var d = OgofConfig.Default;
        if (config.FontSize == default)
            config.FontSize = d.FontSize;
        config.FontColors ??= d.FontColors;
        if (config.EnoughChatters == default)
            config.EnoughChatters = d.EnoughChatters;
        config.ClientId ??= d.ClientId;
        config.BroadcasterId ??= d.BroadcasterId;
        config.ModeratorId ??= d.ModeratorId;
    }
}