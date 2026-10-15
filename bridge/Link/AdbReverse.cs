using System.Diagnostics;

namespace AirYoke.Bridge.Link;

/// <summary>
/// 维持 <c>adb reverse tcp:9000 tcp:9000</c> 这条端口映射。
///
/// ## 为什么需要保活
///
/// adb 的端口映射**会在手机锁屏、USB 重插、adb 服务重启后失效**，而且是静默失效 ——
/// 不会报错，只是数据突然没了。所以这里每隔几秒重新下发一次；
/// <c>adb reverse</c> 本身是幂等的，重复执行无副作用。
/// </summary>
public sealed class AdbReverse : IDisposable
{
    private readonly Action<string> _log;
    private readonly Func<bool>? _hasLiveConnection;
    private readonly CancellationTokenSource _cts = new();
    private Task? _keepAliveTask;

    private string? _adbPath;

    /// <summary>最近一次下发是否成功。映射失效时这里会变成 false。</summary>
    public bool IsActive { get; private set; }

    /// <summary>没找到 adb.exe —— 通道 B 不可用，但不影响通道 A。</summary>
    public bool AdbMissing => _adbPath is null && _searched;

    private bool _searched;

    /// <param name="log">日志输出。</param>
    /// <param name="hasLiveConnection">
    /// 查询当前是否已有活的 TCP 连接。有的话就跳过重发映射，避免打断好链路。
    /// </param>
    public AdbReverse(Action<string> log, Func<bool>? hasLiveConnection = null)
    {
        _log = log;
        _hasLiveConnection = hasLiveConnection;
    }

    public void Start()
    {
        _adbPath = LocateAdb();
        _searched = true;

        if (_adbPath is null)
        {
            _log("未找到 adb.exe —— adb reverse 通道不可用。" +
                 "这不影响 USB 网络共享通道；如需通道 B，请安装 platform-tools 或打开 USB 调试。");
            return;
        }

        _log($"已找到 adb：{_adbPath}");
        _keepAliveTask = Task.Run(KeepAliveLoop);
    }

    private async Task KeepAliveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            // 已经有活连接时不要重发 adb reverse ——
            // 重新下发端口映射会把正在用的隧道拆掉重建，白白打断一条好链路。
            // 只在"没有活连接"时才去确保映射存在。
            if (_hasLiveConnection?.Invoke() != true)
            {
                // 启动时没找到 adb 也要继续找：用户可能中途才把 platform-tools
                // 放到 exe 旁边，或者插上了手机。找不到就一直重试，成本只有一次文件检查。
                _adbPath ??= LocateAdb();

                if (_adbPath is not null)
                {
                    IsActive = await TrySetUpReverse().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(4000, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task<bool> TrySetUpReverse()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _adbPath!,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("reverse");
            psi.ArgumentList.Add($"tcp:{Protocol.Port}");
            psi.ArgumentList.Add($"tcp:{Protocol.Port}");

            using var process = Process.Start(psi);
            if (process is null) return false;

            await process.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception e)
        {
            _log($"adb reverse 失败：{e.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按常见位置找 adb.exe。找不到就说明用户没装 platform-tools，
    /// 这时通道 B 用不了 —— 这是正常情况，不是错误。
    /// </summary>
    private static string? LocateAdb()
    {
        const string exe = "adb.exe";

        // 1) PATH 里
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // PATH 里可能有非法路径项，跳过
            }
        }

        // 2) ANDROID_HOME / ANDROID_SDK_ROOT 指向的 SDK
        foreach (var varName in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var root = Environment.GetEnvironmentVariable(varName);
            if (string.IsNullOrWhiteSpace(root)) continue;
            try
            {
                var candidate = Path.Combine(root, "platform-tools", exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 环境变量里可能是非法路径，跳过
            }
        }

        // 3) 常见安装位置
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk", "platform-tools", exe),
            @"C:\platform-tools\adb.exe",
            @"C:\adb\adb.exe",
            Path.Combine(AppContext.BaseDirectory, "platform-tools", exe),
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
            }
        }

        return null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
