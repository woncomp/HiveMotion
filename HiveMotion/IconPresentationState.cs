namespace HiveMotion;

/// <summary>UI-owned lifetime gate. Resource callbacks never carry a cell from an older view.</summary>
internal sealed class IconPresentationState
{
    public int Generation { get; private set; }
    public bool Active { get; private set; }
    private bool _shown;
    private bool _rendered;
    public bool Transitioning { get; set; }
    public bool CanApply => Active && _shown && _rendered && !Transitioning;

    public void Begin()
    {
        ++Generation;
        Active = true;
        _shown = _rendered = Transitioning = false;
    }

    public void Shown(int generation) { if (generation == Generation && Active) _shown = true; }
    public void Rendered(int generation) { if (generation == Generation && Active) _rendered = true; }
    public void ReplaceContent() => ++Generation;
    public void Close() { ++Generation; Active = false; }
}
