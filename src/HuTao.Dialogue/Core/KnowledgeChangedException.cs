namespace HuTao.Dialogue.Core;

/// <summary>Abort this draft when its SQL evidence changes; do not publish or remember the draft.</summary>
internal sealed class KnowledgeChangedException : OperationCanceledException;
