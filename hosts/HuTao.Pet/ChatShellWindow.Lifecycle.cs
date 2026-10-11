using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using HuTao.Dialogue.Runtime;
using HuTao.Dialogue.Sessions;
using HuTao.Dialogue.Storage;
using HuTao.Foundation;
using HuTao.Foundation.Abstractions;
using HuTao.Foundation.Diagnostics;
using HuTao.Persona;
using HuTao.Pet.ChatShell;
using HuTao.Voice;

namespace HuTao.Pet;


public partial class ChatShellWindow
{
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_closeReady)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_closing)
            return;

        _closing = true;
        _ = CloseAsync();
    }

    /// <summary>
    /// 排空顺序与旧聊天室窗口一致（这也是生命周期回归在守的东西）：
    /// 等启动 → 等当前回合 → 等独立播放 → 逐个回收 runtime → 再关。
    /// 顺序反了会出现"记忆还没落盘窗口就没了"。
    /// </summary>
    private async Task CloseAsync()
    {
        _cts.Cancel();
        try
        {
            try { await _startup; } catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.startup_shutdown", ex); }
            try { await _activeTurn; } catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.turn_shutdown", ex); }

            // 回放按钮可能独立于对话回合在播，等它释放。
            await _audioGate.WaitAsync();
            _audioGate.Release();

            if (_viewModel is not null)
            {
                try { await _viewModel.ShutdownAsync(); }
                catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.viewmodel_shutdown", ex); }
            }

            foreach (var pool in _pools.ToArray())
            {
                try
                {
                    var failures = await pool.DisposeAllAsync();
                    foreach (var failure in failures)
                        await _pendingCleanup.RetireAsync(pool).ConfigureAwait(false);
                }
                catch (Exception ex) { LocalDiagnosticLog.Default.Write("chat_shell.pool_dispose", ex); }
            }
            _pools.Clear();
            try { await _pendingCleanup.RetryAsync(); }
            catch (Exception ex)
            {
                LocalDiagnosticLog.Default.Write("chat_shell.pending_cleanup", ex);
                SetStatus("记忆尚未保存完成。请排除文件占用或磁盘问题后再次关闭。");
                _closeRequest?.TrySetResult(false);
                _closeRequest = null;
                _closing = false;
                return;
            }

            SaveWindowPreferences();
            // 必须在离开 Closing 事件之后再 Close，否则 WPF 会忽略这个重入调用。
            await Dispatcher.Yield(DispatcherPriority.Background);
            _closeReady = true;
            Close();
            _closeRequest?.TrySetResult(true);
        }
        catch (Exception ex)
        {
            LocalDiagnosticLog.Default.Write("chat_shell.close", ex);
            SetStatus("关闭时出错，诊断已写入本机日志。");
            _closeRequest?.TrySetResult(false);
            _closeRequest = null;
        }
        finally
        {
            _closing = false;
        }
    }
}
