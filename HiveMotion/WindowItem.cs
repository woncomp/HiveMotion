using System;

namespace HiveMotion;

public class WindowItem
{
    public int Index { get; set; }
    public IntPtr Handle { get; set; }
    public string Title { get; set; } = string.Empty;

    public string DisplayLabel => $"{Index}. {Title}";
}
