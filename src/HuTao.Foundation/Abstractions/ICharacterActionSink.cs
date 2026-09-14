namespace HuTao.Foundation.Abstractions;

public enum CharacterActionKind
{
    Expression,
    Motion,
    LipSync,
    Notification,
}

/// <summary>与具体 MMD、Live2D 或 VRM SDK 无关的角色动作协议。</summary>
public sealed record CharacterAction(
    CharacterActionKind Kind,
    string Name,
    double Intensity = 1.0,
    TimeSpan? Duration = null,
    IReadOnlyDictionary<string, string>? Parameters = null);

public interface ICharacterActionSink
{
    Task ExecuteAsync(CharacterAction action, CancellationToken ct = default);
}
