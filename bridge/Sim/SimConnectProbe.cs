using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 最小连通性探针：只做"能不能连上模拟器、能不能收发消息"这一件事。
///
/// 单独写它是为了先验证 P/Invoke 这条路走得通 —— 无窗口模式下的
/// <c>SimConnect_CallDispatch</c> 轮询是这套方案里唯一没有十足把握的地方，
/// 值得用最小的代码量先证实，而不是等整个实现写完再一起调。
/// </summary>
public static class SimConnectProbe
{
    /// <summary>必须由静态字段持有：委托被 GC 回收后模拟器回调会踩空指针。</summary>
    private static SimConnectApi.DispatchProc? _dispatch;

    private static int _messageCount;

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  SimConnect 连通性探针");
        Console.WriteLine("  ─────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            Console.Error.WriteLine("  它随模拟器一起安装；确认模拟器已装好，且 UserCfg.opt 里的路径有效。");
            return 1;
        }
        Console.WriteLine($"  DLL: {SimConnectApi.ResolvedPath}");

        _dispatch = OnDispatch;

        // hWnd 传 NULL：本程序没有窗口，改由下面的循环主动 CallDispatch 收消息
        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr handle, "AirYoke Bridge (probe)", IntPtr.Zero, 0, IntPtr.Zero, 0);

        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败，HRESULT=0x{hr:X8}");
            Console.Error.WriteLine("  常见原因：模拟器没在运行，或它还在加载主界面。");
            return 1;
        }

        Console.WriteLine($"  已打开连接，句柄 0x{handle:X}");
        Console.WriteLine("  轮询中（Ctrl+C 退出）…");
        Console.WriteLine();

        var lastReport = DateTime.UtcNow;
        while (!token.IsCancellationRequested)
        {
            // 无窗口模式下由这里主动把消息泵起来
            SimConnectApi.SimConnect_CallDispatch(handle, _dispatch, IntPtr.Zero);

            if ((DateTime.UtcNow - lastReport).TotalSeconds >= 2)
            {
                lastReport = DateTime.UtcNow;
                Console.WriteLine($"    已收到 {_messageCount} 条消息");
            }

            try
            {
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SimConnectApi.SimConnect_Close(handle);
        Console.WriteLine("  已关闭连接。");
        return 0;
    }

    private static void OnDispatch(IntPtr pData, uint cbData, IntPtr pContext)
    {
        if (pData == IntPtr.Zero) return;

        Interlocked.Increment(ref _messageCount);

        uint id = (uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId);

        switch (id)
        {
            case SimConnectApi.RECV_ID_OPEN:
                Console.WriteLine("  ✓ 收到 OPEN —— 与模拟器的会话已建立");
                break;

            case SimConnectApi.RECV_ID_QUIT:
                Console.WriteLine("  ! 模拟器已退出");
                break;

            case SimConnectApi.RECV_ID_EXCEPTION:
            {
                int code = Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetException);
                Console.WriteLine($"  ! SimConnect 异常，代码 {code}");
                break;
            }

            default:
                // 其它消息在探针阶段不关心，计数即可
                break;
        }
    }
}
