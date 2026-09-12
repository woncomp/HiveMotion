namespace HiveMotion;

/// <summary>Copied icon identity; constructing this value never queries files or processes.</summary>
public sealed record IconRequest(string CustomPath = "", string ExecutablePath = "", string Glyph = "")
{
    public static readonly IconRequest Empty = new();

    public static IconRequest ForMotion(Motion motion) => new(motion.IconPath,
        motion is ApplicationMotion app && app.IsConfigured ? app.ExecutablePath : "",
        motion switch
        {
            ApplicationMotion { IsConfigured: false } => "\uE71D",
            SystemActionMotion action => SystemActions.Find(action.ActionId)?.IconGlyph ?? "\uE713",
            WindowViewMotion => "\uE7C4",
            _ => ""
        });
}
