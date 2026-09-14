namespace HuTao.Foundation.Abstractions;

/// <summary>
/// 「一个子进程失败了，附带退出码与诊断输出」。
///
/// 为什么要抽这个接口：诊断日志（Foundation 层）需要记录退出码与 stderr，
/// 而具体的异常类型 <c>TtsProcessException</c> 属于 Voice 层。
/// 如果日志直接 `is TtsProcessException`，就形成 **Foundation → Voice** 的反向依赖，
/// 拆程序集时立刻变成编译错误。
///
/// 用接口把依赖倒过来：日志只认这个契约，Voice 层的异常去实现它。
/// 这是**唯一**需要为解耦引入的接口——其余地方都是删掉硬编码就够了，
/// 不要照着这个模式到处抽接口（单进程内部不需要那么多间接层）。
/// </summary>
public interface IProcessFailure
{
    /// <summary>子进程退出码。</summary>
    int ExitCode { get; }

    /// <summary>子进程的诊断输出（stderr）。只用于落日志，绝不作为角色台词。</summary>
    string Diagnostic { get; }
}
