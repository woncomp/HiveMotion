namespace HiveMotion;

/// <summary>Uniquely identifies one Windows process lifetime and therefore cannot survive PID reuse.</summary>
public readonly record struct ProcessInstanceKey(uint ProcessId, long CreationFileTime)
{
    public bool IsKnown => ProcessId != 0 && CreationFileTime != 0;
}
