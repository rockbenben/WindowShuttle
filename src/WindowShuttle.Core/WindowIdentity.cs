namespace WindowShuttle.Core;

/// <summary>A process lifetime and a token attached to one particular native window.</summary>
public sealed record WindowIdentity(uint ProcessId, long ProcessCreated, long WindowToken)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsComplete => ProcessId != 0 && ProcessCreated > 0 && WindowToken > 0;
}

public enum WindowIdentityStatus { Match, Different, Unavailable }
