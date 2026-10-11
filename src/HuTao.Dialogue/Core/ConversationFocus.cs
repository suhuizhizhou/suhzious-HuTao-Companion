using System.Text.RegularExpressions;
using HuTao.Foundation.Abstractions;

namespace HuTao.Dialogue.Core;

/// <summary>Limits working context after an explicit topic boundary; persisted history is unchanged.</summary>
public static class ConversationFocus
{
    public static bool StartsNewTopic(string? input) => input is not null && Regex.IsMatch(input,
        @"^\s*(?:好了[，,。]?\s*)?(?:换(?:个|一个)?话题|(?:先)?不(?:聊|说|谈)这个)(?:[，,。！!：:\s]|$)");

    public static ChatMessage[] ActiveHistory(IReadOnlyList<ChatMessage> history)
    {
        for(var i=history.Count-1;i>=0;i--)
            if(history[i].Role=="user" && StartsNewTopic(history[i].Content))
                return history.Skip(i).ToArray();
        return history.ToArray();
    }
}
