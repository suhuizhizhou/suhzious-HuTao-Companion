using HuTao.Foundation.Abstractions;
using HuTao.Dialogue.Immersion;
using HuTao.Knowledge.Memory;
using HuTao.Persona;
using HuTao.Dialogue.Storage;
using HuTao.Voice;
using HuTao.Knowledge.Rag;
using HuTao.Dialogue.Tools;
using System.Diagnostics;
using System.Text;
using HuTao.Foundation.Diagnostics;

namespace HuTao.Dialogue.Core;

// 控制台整轮语音适配；桌面逐段投递使用 SpeechDeliverySession。
public sealed partial class ReactAgent
{
    private async Task<AgentTurnResult> RunFullTurnAsync(
        string? userInput,
        bool isProactive,
        CancellationToken ct)
    {
        var text = await GenerateTextCoreAsync(userInput, isProactive, ct)
            .ConfigureAwait(false);
        if(!_recall.KnowledgePrivacyIsCurrent(text.Recall))throw new KnowledgeChangedException();
        foreach(var segment in text.Segments)segment.DemandDelivery();
        var spoken = text.Segments
            .Where(segment => !SpeechText.IsAction(segment.Text))
            .ToList();

        TtsResult? audio = null;
        var action = "已生成台词";
        if (spoken.Count == 1 && spoken[0].OriginalAudioPath is { } originalPath)
        {
            audio = new TtsResult(originalPath, 0, 0);
            action = $"已直接采用角色原声 {spoken[0].OriginalVoiceId}";
        }
        else if (_speechSynthesizer.IsAvailable)
        {
            try
            {
                if (spoken.Count == 0)
                {
                    action = "仅包含动作气泡，已跳过语音合成";
                }
                else
                {
                    var first = spoken[0];
                    // 只送**可读部分**给引擎：括号里的动作/旁白去掉（整段是动作的已在上面滤掉，
                    // 但「（撑着下巴）客官今日来得早啊」这种夹在台词里的必须在这一层剥），
                    // 破折号换成会被 cut5 切句的标点，否则听起来完全没有停顿。
                    var speakable = spoken
                        .Select(segment => SpeechText.ForSpeech(segment.Text, SpeechText.DashPause))
                        .Where(line => line.Length > 0)
                        .ToArray();
                    if (speakable.Length == 0)
                    {
                        action = "仅包含动作与括号旁白，已跳过语音合成";
                    }
                    else
                    {
                    audio = await _speechSynthesizer.SynthesizeAsync(
                        string.Join('\n', speakable),
                        first.Emotion,
                        first.Intensity,
                        ct).ConfigureAwait(false);
                    action = audio is null
                        ? "语音引擎未返回音频"
                        : $"已合成语音 {audio.AudioPath}";
                    }
                }
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
                HuTao.Foundation.Diagnostics.LocalDiagnosticLog.Default.Write("speech.full_turn", ex);
                action = "已生成文字";
            }
        }

        if(!_recall.KnowledgePrivacyIsCurrent(text.Recall))throw new KnowledgeChangedException();
        foreach(var segment in text.Segments)segment.DemandDelivery();
        await PublishAsync(new AgentStageEvent(
            AgentStage.Act,
            action,
            DateTimeOffset.Now), ct).ConfigureAwait(false);
        if (audio is not null)
        {
            await PublishAsync(new AgentAudioEvent(
                audio.AudioPath,
                spoken.Count == 1 ? spoken[0].OriginalVoiceId : null,
                DateTimeOffset.Now), ct).ConfigureAwait(false);
        }

        return new AgentTurnResult(
            text.Reason,
            text.Observation,
            text.Reply,
            audio,
            action) { Story=text.Story, StoryAnswer=text.StoryAnswer, ConversationSupport=text.ConversationSupport, Immersion=text.Immersion, ImmersionPath=text.ImmersionPath, Retrieval=text.Retrieval, Recall=text.Recall, ToolRuns=text.ToolRuns,
                       // 把分段带出去：一段一个气泡。漏了这一行，聊天窗就只能渲染成一个大气泡。
                       Segments=text.Segments };
    }

}
