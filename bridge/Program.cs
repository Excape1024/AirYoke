using System.Text;
using AirYoke.Bridge.Link;
using AirYoke.Bridge.Sim;

namespace AirYoke.Bridge;

/// <summary>
/// AirYoke 桥接程序：把手机通过 USB 线发来的操纵量送进微软模拟飞行。
///
/// 同时开两条通道，用哪条由手机决定：
///   A. USB 网络共享 + UDP（首选，无队头阻塞，适合 100Hz 连续操纵流）
///   B. adb reverse + TCP（自动回退，手机侧零网络配置）
///
/// 两条通道复用同一套二进制协议，见 <see cref="Protocol"/>。
/// </summary>
public static class Program
{
    private static readonly object ConsoleLock = new();
    private static bool _interactive = true;

    private static ISimOutput? _sim;
    private static long _receivedTotal;

    /// <summary>
    /// 最新一帧，供状态行显示。
    ///
    /// 用锁而不是裸字段：`Protocol.State` 是含 9 个字段的结构体，
    /// 赋值不是原子的 —— 100Hz 的接收线程与状态线程并发时可能读到"半个新帧 + 半个旧帧"，
    /// 而且这个撕裂值会被显示满 2 秒。锁的争用可以忽略（每 2 秒读一次）。
    /// </summary>
    private static readonly object StateLock = new();
    private static Protocol.State _latestState;
    private static double _measuredHz;
    private static long _rateBaseCount;
    private static DateTime _rateWindowAt = DateTime.UtcNow;
    private static string _statusLine = "启动中…";

    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            bool passed = SelfTest();
            Console.WriteLine(passed ? "协议自检通过" : "协议自检失败");
            return passed ? 0 : 1;
        }
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        // 状态监视：查"AP/A-THR 反复断开"这类问题，在警报响的时候看
        if (args.Contains("--watch"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var watchCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                watchCts.Cancel();
            };
            return await StateWatch.RunAsync(watchCts.Token).ConfigureAwait(false);
        }

        // MobiFlight WASM 模块探针：验证模块可用，并列出襟翼/减速板的 LVar 名
        if (args.Contains("--mftest"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var mfCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                mfCts.Cancel();
            };
            return await MobiFlightProbe.RunAsync(mfCts.Token).ConfigureAwait(false);
        }

        // 反推实测：会真的推油门，**只能在地面、发动机运转时**运行
        if (args.Contains("--reversetest"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var reverseCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                reverseCts.Cancel();
            };
            return await ReverseProbe.RunAsync(reverseCts.Token).ConfigureAwait(false);
        }

        // 襟翼/减速板/油门回读实测：会拨动襟翼和减速板，请在停机状态下运行
        if (args.Contains("--controltest"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var controlCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                controlCts.Cancel();
            };
            return await ControlProbe.RunAsync(controlCts.Token).ConfigureAwait(false);
        }

        // 操纵轴方向实测：会短暂拨动操纵面，请在停机状态下运行
        if (args.Contains("--axistest"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var axisCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                axisCts.Cancel();
            };
            return await AxisSignProbe.RunAsync(axisCts.Token).ConfigureAwait(false);
        }

        // 连通性探针：只验证 SimConnect 这条链路，不启动手机侧
        if (args.Contains("--simtest"))
        {
            Console.OutputEncoding = Encoding.UTF8;
            using var probeCts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                probeCts.Cancel();
            };
            return await SimConnectProbe.RunAsync(probeCts.Token).ConfigureAwait(false);
        }

        Console.OutputEncoding = Encoding.UTF8;
        _interactive = !Console.IsOutputRedirected;

        PrintBanner();

        if (!SelfTest())
        {
            Console.Error.WriteLine("协议自检失败，拒绝启动。");
            return 1;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var udp = new UdpServer(OnState, Log);
        using var tcp = new TcpServer(OnState, Log);
        using var adb = new AdbReverse(Log, hasLiveConnection: () => tcp.Connected);
        using ISimOutput sim = args.Contains("--dry-run") ? new DryRunOutput() : new SimConnectOutput(Log);
        _sim = sim;

        if (sim is SimConnectOutput connected && connected.Connect())
        {
            Log($"模拟器输出：{sim.Name}");
        }
        else
        {
            Log("模拟器未连接 —— 手机数据只会显示，不会驱动飞机。");
            Log("模拟器启动后会自动重试连接。");
        }

        adb.Start();
        bool udpOk = udp.Start();
        bool tcpOk = tcp.Start();

        // 两条监听都起不来就没必要继续了 —— 再跑下去也只是假装在工作
        if (!udpOk && !tcpOk)
        {
            Console.Error.WriteLine(
                $"  端口 {Protocol.Port} 的 UDP 和 TCP 都监听失败，无法继续。");
            return 1;
        }

        if (_interactive)
        {
            Console.WriteLine();
            Console.WriteLine("  等待手机连接… 按 Ctrl+C 退出。");
            Console.WriteLine();
        }

        // 非交互模式（输出被重定向）也要出状态，否则日志里什么都看不到。
        // 区别只在刷新方式，见 RenderStatus。
        var statusTask = Task.Run(() => StatusLoop(cts.Token), cts.Token);

        try
        {
            await RunLoop(udp, tcp, sim, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        try { await statusTask.ConfigureAwait(false); } catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("  已退出。");
        return 0;
    }

    private static async Task RunLoop(
        UdpServer udp, TcpServer tcp, ISimOutput sim, CancellationToken token)
    {
        int telemetrySeq = 0;
        var lastTelemetryAt = DateTime.UtcNow;
        var lastSimRetryAt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            // 模拟器可能比桥接程序晚启动（或中途重启），所以要持续重试。
            // 每 5 秒一次，避免连接失败时刷屏。
            if (sim is SimConnectOutput reconnect && !sim.IsConnected && (DateTime.UtcNow - lastSimRetryAt).TotalSeconds >= 5)
            {
                lastSimRetryAt = DateTime.UtcNow;
                if (reconnect.Connect())
                {
                    Log("已连接模拟器");
                }
            }

            // 两条通道都可能来数据，取最近有包的那条。
            // 实际使用中只有一条活跃，但断线重连的瞬间可能两条都在。
            bool fromUdp = udp.LastReceiveUtc >= tcp.LastReceiveUtc;
            var lastReceive = fromUdp ? udp.LastReceiveUtc : tcp.LastReceiveUtc;
            bool linkUp = lastReceive != DateTime.MinValue
                          && (DateTime.UtcNow - lastReceive).TotalMilliseconds < 1500;

            UpdateRate();

            long received = Interlocked.Read(ref _receivedTotal);
            _statusLine = linkUp
                ? $"已连接  {(fromUdp ? "USB 网络共享 (UDP)" : "adb reverse (TCP)")}  " +
                  $"{(fromUdp ? udp.RemoteDescription : tcp.RemoteDescription)}  " +
                  $"{_measuredHz,4:F0}Hz  共 {received} 帧"
                : $"等待手机…   A: {udp.RemoteDescription}   B: {tcp.RemoteDescription}";

            // 10Hz 回遥测
            if ((DateTime.UtcNow - lastTelemetryAt).TotalMilliseconds >= 100)
            {
                lastTelemetryAt = DateTime.UtcNow;
                var telemetry = sim.BuildTelemetry(telemetrySeq++);
                udp.SendTelemetry(telemetry);
                tcp.SendTelemetry(telemetry);
            }

            await Task.Delay(10, token).ConfigureAwait(false);
        }
    }

    private static void UpdateRate()
    {
        var now = DateTime.UtcNow;
        double elapsed = (now - _rateWindowAt).TotalSeconds;
        if (elapsed < 0.5) return;

        long received = Interlocked.Read(ref _receivedTotal);
        _measuredHz = (received - _rateBaseCount) / elapsed;
        _rateBaseCount = received;
        _rateWindowAt = now;
    }

    private static void OnState(Protocol.State state)
    {
        lock (StateLock)
        {
            if (_sim is DryRunOutput && (state.Buttons != _latestState.Buttons || state.Aux != _latestState.Aux ||
                state.Throttle != _latestState.Throttle || state.Flags != _latestState.Flags))
                Log($"空转输入：flags={state.Flags} throttle={state.Throttle} buttons={state.Buttons} aux={state.Aux}");
            _latestState = state;
        }
        Interlocked.Increment(ref _receivedTotal);
        _sim?.Apply(state);
    }

    private static Protocol.State LatestState()
    {
        lock (StateLock)
        {
            return _latestState;
        }
    }

    private static async Task StatusLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            RenderStatus();
            try
            {
                await Task.Delay(_interactive ? 200 : 2000, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 单行刷新状态。用 <c>\r</c> 回到行首重写，而不是用光标定位画多行 ——
    /// 后者在窗口大小变化、输出被重定向、终端不支持时很容易错乱。
    /// </summary>
    private static void RenderStatus()
    {
        var s = LatestState();
        var sb = new StringBuilder();
        sb.Append(_statusLine);

        if (s.Flags != 0 || s.Seq != 0)
        {
            sb.Append("  │  ");
            sb.Append($"升降 {s.ElevatorNorm,6:+0.00;-0.00; 0.00}  ");
            sb.Append($"副翼 {s.AileronNorm,6:+0.00;-0.00; 0.00}  ");
            sb.Append($"方向 {s.RudderNorm,6:+0.00;-0.00; 0.00}  ");
            sb.Append($"油门 {s.ThrottleNorm,6:+0.00;-0.00; 0.00}  ");
            sb.Append(s.Calibrated ? "已校准" : "未校准");
            // 显示控制权归属：排查自动油门警报时，这一栏直接说明问题在不在我们这边
            sb.Append(s.ThrottleEngaged ? "  [油门:手机接管]" : "  [油门:放行给飞机]");
        }

        string text = sb.ToString();
        lock (ConsoleLock)
        {
            if (!_interactive)
            {
                // 重定向到文件时不做 \r 覆盖，直接按行输出，日志才读得懂
                Console.WriteLine(text);
                Console.Out.Flush();
                return;
            }

            int width = SafeWindowWidth();
            if (text.Length < width) text = text.PadRight(width - 1);
            else text = text[..(width - 1)];
            Console.Write('\r');
            Console.Write(text);
        }
    }

    private static int SafeWindowWidth()
    {
        try
        {
            return Math.Max(40, Console.WindowWidth);
        }
        catch
        {
            return 120;
        }
    }

    private static void Log(string message)
    {
        lock (ConsoleLock)
        {
            Console.Write('\r');
            Console.Write(new string(' ', SafeWindowWidth() - 1));
            Console.Write('\r');
            Console.WriteLine("  " + message);
        }
    }

    /// <summary>
    /// 启动时自检 CRC。
    ///
    /// 这一步值得单独做：若手机与 PC 的 CRC 算不到一块去，**不会报任何错**，
    /// 表现是每个包都被静默丢弃、界面一直显示"未连接"，而链路看起来毫无问题。
    /// 拿标准向量对一次，能把这类问题在启动瞬间暴露出来。
    /// </summary>
    private static bool SelfTest()
    {
        var vector = Encoding.ASCII.GetBytes("123456789");
        ushort crc = Protocol.Crc16(vector);
        if (crc != 0x29B1)
        {
            Console.Error.WriteLine($"CRC 自检失败：期望 0x29B1，实际 0x{crc:X4}。");
            return false;
        }

        // 顺带确认编解码往返无损
        var buf = new byte[Protocol.StateSize];
        var probe = new Protocol.State(
            Seq: 1, Flags: Protocol.FlagCalibrated,
            Elevator: -12345, Aileron: 6789, Rudder: -1, Throttle: 32767,
            Buttons: 0b1010, Aux: 0, TimestampMs: 42);
        Protocol.EncodeState(probe, buf);
        if (!Protocol.TryDecodeState(buf, out var back) || !back.Equals(probe))
        {
            Console.Error.WriteLine("协议往返自检失败。");
            return false;
        }

        return ControlSelfTest.Run();
    }

    private static void PrintBanner()
    {
        Console.WriteLine();
        Console.WriteLine("  AirYoke 桥接程序 —— 手机当客机操纵盘");
        Console.WriteLine("  ─────────────────────────────────────────");
        Console.WriteLine($"  监听端口 {Protocol.Port}（UDP 广播 + TCP 回环）");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            用法: AirYoke.Bridge [选项]

            把手机通过 USB 线发来的姿态数据送进微软模拟飞行。

            两条通道会自动就绪，用哪条由手机决定：
              A. USB 网络共享 + UDP   手机打开「USB 网络共享」即可，免装驱动
              B. adb reverse + TCP    需要手机开启 USB 调试

            选项:
              -h, --help    显示本帮助

              --selftest   仅运行协议自检
              --dry-run    仅验证手机链路，不连接模拟器

            版本 1.4 使用协议四，请与 1.4 手机端配套使用。
            """);
    }
}
