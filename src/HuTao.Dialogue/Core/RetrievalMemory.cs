using HuTao.Knowledge.Rag;

namespace HuTao.Dialogue.Core;

/// <summary>一次检索轮次在记忆里的形态：问了什么、拿到哪些证据、覆盖了哪些事实组。</summary>
public sealed record RetrievalTurn(
    string Query,
    IReadOnlyList<StoryEvidence> Evidence,
    IReadOnlyList<string> Facts,
    string Status,
    bool Sufficient);

/// <summary>
/// agent 的**检索记忆**。跨轮持久，让「多级多次查询」不必每次从零开始。
///
/// 它刻意不是对话历史（那是 <c>ChatMessage</c> 的职责），而是检索侧的工作记忆：
/// · 已经拿到的证据 id → 可直接复用，不必重新检索；
/// · 已经问过的查询 → 不重复问；
/// · 已经覆盖的事实组 → 不再当缺口；
/// · 还缺的事实组 + 最近几轮的查询 → 是**下一轮改写查询的输入**。
///
/// 这是「多级多次查询交给 agent」在检索侧的落点：上一轮观察决定这一轮问什么，
/// 而不是开局把查询一次性定死。
/// </summary>
public sealed class RetrievalMemory(int capacity = 24)
{
    private readonly LinkedList<RetrievalTurn> _turns = new();
    private readonly Dictionary<string, StoryEvidence> _heldEvidence = new(StringComparer.Ordinal);
    private readonly HashSet<string> _askedQueries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _heldFacts = new(StringComparer.Ordinal);

    /// <summary>记忆里持有的证据 id（可复用，不必重新检索）。</summary>
    public IReadOnlyCollection<string> HeldEvidenceIds => _heldEvidence.Keys;

    /// <summary>
    /// 记忆里持有的证据本体（按分数降序）。
    /// **这是「利用之前查询的信息」的实体**：新一轮可以直接把它并进证据池，
    /// 而不必为同一件事再检索一遍。
    /// </summary>
    public IReadOnlyList<StoryEvidence> HeldEvidence() =>
        _heldEvidence.Values.OrderByDescending(e => e.Score).ToArray();

    /// <summary>按时间正序的轮次记录。</summary>
    public IReadOnlyList<RetrievalTurn> Turns => _turns.ToArray();

    public bool Holds(string evidenceId) => _heldEvidence.ContainsKey(evidenceId);

    public bool HasAsked(string query) => _askedQueries.Contains(query);

    public bool HoldsFact(string fact) => _heldFacts.Contains(fact);

    public void Record(RetrievalTurn turn)
    {
        _turns.AddLast(turn);
        _askedQueries.Add(turn.Query);
        foreach (var e in turn.Evidence) _heldEvidence[e.Id] = e;
        foreach (var fact in turn.Facts) _heldFacts.Add(fact);
        while (_turns.Count > Math.Max(1, capacity)) _turns.RemoveFirst();
        _heldEvidence.Clear();
        _askedQueries.Clear();
        _heldFacts.Clear();
        foreach (var retained in _turns)
        {
            _askedQueries.Add(retained.Query);
            foreach (var evidence in retained.Evidence) _heldEvidence[evidence.Id] = evidence;
            foreach (var fact in retained.Facts) _heldFacts.Add(fact);
        }
    }

    /// <summary>最近 n 轮问过的查询（倒序），给下一轮改写当输入。</summary>
    public IReadOnlyList<string> RecentQueries(int n) =>
        _turns.Reverse().Take(n).Select(t => t.Query).ToArray();
}
