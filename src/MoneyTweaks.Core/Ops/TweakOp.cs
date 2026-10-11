namespace MoneyTweaks.Core.Ops;

public enum OpState { Applied, NotApplied, NotApplicable, Unknown }

/// <summary>Serializable snapshot of whatever an op changed, used to restore the original value.</summary>
public sealed class OpBackup
{
    public string Kind { get; set; } = "";
    public Dictionary<string, string?> Data { get; set; } = [];
}

/// <summary>A single reversible system change. Every op must be able to capture, apply, check and undo.</summary>
public abstract class TweakOp
{
    public abstract bool RequiresAdmin { get; }
    public abstract string Describe();
    public abstract OpState Check();
    public abstract OpBackup Capture();
    public abstract void Apply();
    /// <summary>Restore from <paramref name="backup"/>; when null, fall back to the Windows default.</summary>
    public abstract void Undo(OpBackup? backup);
}

public class TweakOpException(string message, Exception? inner = null) : Exception(message, inner);
