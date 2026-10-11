using System.Globalization;
using System.Security.Cryptography;

namespace HuTao.Voice;

/// <summary>
/// 生成音频的文件名规则。**唯一定义处**：两个引擎（按需 / 常驻）都必须用它，
/// 否则一边改了前缀或后缀，就会让 <c>OwnedAudioGuard</c> 的删除白名单失配
/// ——那是一条删除权限判定，不是格式偏好。
/// </summary>
public static class GeneratedAudioNaming
{
    /// <summary>引擎产物前缀。<c>OwnedAudioGuard</c> 靠它区分"我们的文件"与用户的文件。</summary>
    public const string Prefix = "agent_tts_";

    public const string Extension = ".wav";

    /// <summary>
    /// 形如 <c>agent_tts_20260214_201609_031_9f3a1c07.wav</c>。
    ///
    /// 时间戳精确到毫秒仍然会撞：两个 agent 在同一毫秒合成（切会话后池里两个角色
    /// 的回合重叠时完全可能）会得到同一个名字，而写入用的是 <see cref="FileMode.CreateNew"/>，
    /// 于是第二次合成直接抛 <c>IOException</c>。补一段随机段消除碰撞，
    /// 同时**保留** CreateNew —— 真撞上了要报错，不能悄悄覆盖别人的音频。
    /// </summary>
    public static string NewFileName(DateTime now)
        => $"{Prefix}{now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)}_{RandomSuffix()}{Extension}";

    private static string RandomSuffix()
        => RandomNumberGenerator.GetHexString(8).ToLowerInvariant();
}
