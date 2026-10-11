namespace HuTao.Dialogue.Storage;

/// <summary>
/// 「这个音频文件是不是我们生成的、可以安全删除的」的唯一定义。
///
/// 抽出来是因为它被两处使用：<see cref="ChatLogStore"/> 的配额清理，
/// 以及会话存储的音频清理。**这是一条删除权限的判定**——如果两处各写一份，
/// 将来只修一处就会让另一条路径变成越权删除（删到用户自己的音频或仓库外的文件）。
///
/// 四道条件缺一不可：
/// 1. 父目录**恰好**是生成目录（不是它的子目录，也不是任何别的地方）；
/// 2. 文件名以 <c>agent_tts_</c> 开头（引擎的产物前缀，见 TtsRuntimeFactory）；
/// 3. 扩展名是 <c>.wav</c>；
/// 4. 文件与其所在目录链上**没有重解析点**（软链接/junction）——
///    这是防止有人用符号链接把"生成目录里的一个名字"指到仓库外的真实文件上。
/// </summary>
public static class OwnedAudioGuard
{
    public const string GeneratedPrefix = "agent_tts_";
    public const string GeneratedExtension = ".wav";

    /// <summary><paramref name="generatedAudioRoot"/> 为 null 时一律拒绝（未配置 = 不允许删除）。</summary>
    public static bool IsOwned(string? generatedAudioRoot, string? audioPath)
    {
        if (string.IsNullOrWhiteSpace(generatedAudioRoot) || string.IsNullOrWhiteSpace(audioPath))
            return false;

        try
        {
            var root = Path.GetFullPath(generatedAudioRoot);
            var path = Path.GetFullPath(audioPath);

            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!Path.GetFileName(path).StartsWith(GeneratedPrefix, StringComparison.Ordinal))
                return false;
            if (!string.Equals(Path.GetExtension(path), GeneratedExtension, StringComparison.OrdinalIgnoreCase))
                return false;
            if (!File.Exists(path))
                return false;

            for (var directory = new DirectoryInfo(root); directory is not null; directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return false;

            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }
}
