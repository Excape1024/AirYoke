using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 实测各操纵轴**可用的**事件名与正负方向。
///
/// 这件事不能靠猜，两个原因：
///  1. `AXIS_ELEVATOR_SET` 的 "+" 到底对应抬头还是低头，各家文档说法不一；
///  2. 更要命的是**有些事件名在 MSFS 2024 里根本不生效**，而
///     `SimConnect_MapClientEventToSimEvent` 对无效名字**不报错** ——
///     它会照常返回成功，然后在发送时静默失效。这种失败没有任何提示。
///
/// 所以这里对每个轴试一组候选事件名，给每个候选发一个已知的正值，
/// 再回读模拟器自己的 `* POSITION` 变量。**让模拟器自己回答哪个名字有效、方向如何。**
///
/// 注意：会短暂拨动操纵面，请在停机状态下运行。
/// </summary>
public static class AxisSignProbe
{
    private const uint DefineIdBase = 100;
    private const uint RequestIdBase = 200;

    /// <summary>机型名查询。油门曲线是机型相关的（有没有反推段完全不同），
    /// 所以每次实测都要先报出到底在测哪架飞机，否则结论没法用。</summary>
    private const uint TitleDefineId = 300;
    private const uint TitleRequestId = 300;

    private const int TitleBufferSize = 256;

    /// <summary>SIMCONNECT_RECV_SIMOBJECT_DATA 里 dwRequestID 的偏移，用来把回包分派到对应轴。</summary>
    private const int RecvOffsetRequestId = 12;

    /// <summary>测试用的轴量。SimConnect 轴量程是 ±16383。</summary>
    private const int TestValue = 8000;

    /// <summary>位置回读的容差。低于这个幅度视为"没反应"。</summary>
    private const double EffectiveThreshold = 0.05;

    /// <summary>每个轴：回读用的 SimVar，以及要试的事件名候选。</summary>
    private static readonly AxisCase[] Cases =
    {
        new("副翼", "AILERON POSITION",
            new[] { "AXIS_AILERONS_SET", "AILERON_SET", "AXIS_AILERON_SET" }),

        new("升降舵", "ELEVATOR POSITION",
            new[] { "AXIS_ELEVATOR_SET", "ELEVATOR_SET", "AXIS_ELEV_SET", "ELEVATOR_AXIS_SET" }),

        new("方向舵", "RUDDER POSITION",
            new[] { "AXIS_RUDDER_SET", "RUDDER_SET", "AXIS_RUDDER_SET_EX1" }),

        new("油门", "GENERAL ENG THROTTLE LEVER POSITION:1",
            new[] { "AXIS_THROTTLE_SET", "THROTTLE_SET", "AXIS_THROTTLE1_SET", "THROTTLE1_SET" }),
    };

    private sealed record AxisCase(string Title, string SimVar, string[] EventCandidates);

    private static SimConnectApi.DispatchProc? _dispatch;
    private static readonly double[] Positions = new double[Cases.Length];

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  操纵轴事件名与方向实测");
        Console.WriteLine("  ─────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            return 1;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr h, "AirYoke Axis Probe", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败，HRESULT=0x{hr:X8}");
            return 1;
        }

        // **每个轴一个独立的数据定义**，而不是把四个 SimVar 塞进一个定义里。
        //
        // 原因：把多个 SimVar 打包成一个定义时，只要有一个名字无效，
        // 后面的条目就会整体前移，而回读端的偏移是写死的 —— 读到的就成了别的轴的值，
        // 表现和"事件没生效"一模一样，极容易误判。分开订阅后每个轴互不影响。
        Console.WriteLine("  订阅回读变量（每个轴独立定义）：");
        for (int i = 0; i < Cases.Length; i++)
        {
            uint defId = DefineIdBase + (uint)i;
            uint reqId = RequestIdBase + (uint)i;

            int addHr = SimConnectApi.SimConnect_AddToDataDefinition(
                h, defId, Cases[i].SimVar, "Percent Over 100",
                SimConnectApi.DATATYPE_FLOAT64, 0f, 0);

            int reqHr = SimConnectApi.SimConnect_RequestDataOnSimObject(
                h, reqId, defId, SimConnectApi.OBJECT_ID_USER,
                SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0);

            Console.WriteLine(
                $"      {Cases[i].SimVar,-40} " +
                $"add={(SimConnectApi.Succeeded(addHr) ? "ok" : $"0x{addHr:X8}")}  " +
                $"req={(SimConnectApi.Succeeded(reqHr) ? "ok" : $"0x{reqHr:X8}")}");
        }

        // 机型名：油门曲线随机型变化（有没有反推段完全不同），
        // 所以每次实测都要先报出到底在测哪一架，否则结论没法用。
        SimConnectApi.SimConnect_AddToDataDefinition(
            h, TitleDefineId, "TITLE", null, SimConnectApi.DATATYPE_STRING256, 0f, 0);
        SimConnectApi.SimConnect_RequestDataOnSimObject(
            h, TitleRequestId, TitleDefineId, SimConnectApi.OBJECT_ID_USER,
            SimConnectApi.PERIOD_SECOND, 0, 0, 0, 0);
        Console.WriteLine();

        await PumpAsync(h, 60, token).ConfigureAwait(false);

        Console.WriteLine($"  当前机型：{(string.IsNullOrWhiteSpace(_aircraftTitle) ? "（未获取到，可能还在主菜单）" : _aircraftTitle)}");
        Console.WriteLine();

        var working = new List<string>();

        for (int axisIndex = 0; axisIndex < Cases.Length; axisIndex++)
        {
            var testCase = Cases[axisIndex];
            Console.WriteLine($"  {testCase.Title}（回读 {testCase.SimVar}）");

            string? winner = null;
            double winnerSign = 0;
            uint winnerId = 0;

            foreach (var name in testCase.EventCandidates)
            {
                if (token.IsCancellationRequested) break;

                uint eventId = (uint)(100 + axisIndex * 10 + Array.IndexOf(testCase.EventCandidates, name));

                int mapHr = SimConnectApi.SimConnect_MapClientEventToSimEvent(h, eventId, name);
                if (!SimConnectApi.Succeeded(mapHr))
                {
                    Console.WriteLine($"      {name,-24} 映射失败 0x{mapHr:X8}");
                    continue;
                }

                double positive = await PulseAsync(h, eventId, axisIndex, TestValue, token)
                    .ConfigureAwait(false);
                double negative = await PulseAsync(h, eventId, axisIndex, -TestValue, token)
                    .ConfigureAwait(false);
                await PulseAsync(h, eventId, axisIndex, 0, token).ConfigureAwait(false);

                double span = Math.Abs(positive - negative);

                if (span < EffectiveThreshold)
                {
                    Console.WriteLine($"      {name,-24} 无效（位置无变化）");
                    continue;
                }

                double sign = Math.Sign(positive);
                Console.WriteLine($"      {name,-24} 有效   发正 -> {positive,7:+0.000;-0.000; 0.000}   " +
                                  $"方向：{(sign > 0 ? "同号" : "反号")}");

                if (winner is null)
                {
                    winner = name;
                    winnerSign = sign;
                    winnerId = eventId;
                }
            }

            if (winner is not null)
            {
                working.Add($"  {testCase.Title,-6} 用 {winner,-22} " +
                            $"方向 {(winnerSign > 0 ? "同号（不用反转）" : "反号（需反转）")}");
            }
            else
            {
                working.Add($"  {testCase.Title,-6} **没有可用的事件名**");
            }
            Console.WriteLine();

            // 油门单独量一条传递曲线：实测发现它不是线性的
            // （发 +8000 得到手柄 +0.744，而非 8000/16383=0.488），
            // 而反推段的行程取决于这条曲线，必须量准而不是假设线性。
            if (winner is not null && testCase.Title == "油门")
            {
                await SweepThrottleAsync(h, winnerId, axisIndex, token).ConfigureAwait(false);
            }
        }

        SimConnectApi.SimConnect_Close(h);

        Console.WriteLine("  ══════ 结论 ══════");
        foreach (var line in working) Console.WriteLine(line);
        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// 量油门的传递曲线。
    ///
    /// 别的轴都是线性的（发 ±8000 正好得到 ±0.488 = ±8000/16383），
    /// 唯独油门不是。反推段的行程靠这条曲线确定，所以逐点量出来，不靠假设。
    /// </summary>
    private static async Task SweepThrottleAsync(
        IntPtr h, uint eventId, int axisIndex, CancellationToken token)
    {
        Console.WriteLine("  油门传递曲线（发出的轴值 -> 手柄实际位置）：");

        int[] values = { 16383, 12287, 8191, 6143, 4095, 2047, 0, -2048, -4096, -8192, -12288, -16383 };
        foreach (int v in values)
        {
            if (token.IsCancellationRequested) break;
            double pos = await PulseAsync(h, eventId, axisIndex, v, token).ConfigureAwait(false);
            Console.WriteLine($"      {v,7} -> {pos,7:+0.0000;-0.0000; 0.0000}");
        }

        // 归到慢车
        await PulseAsync(h, eventId, axisIndex, 0, token).ConfigureAwait(false);
        Console.WriteLine();
    }

    /// <summary>发一个值，泵几轮，返回该轴的位置。</summary>
    private static async Task<double> PulseAsync(
        IntPtr h, uint eventId, int axisIndex, int value, CancellationToken token)
    {
        SimConnectApi.SimConnect_TransmitClientEvent(
            h, SimConnectApi.OBJECT_ID_USER, eventId, unchecked((uint)value), 0,
            SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);

        await PumpAsync(h, 30, token).ConfigureAwait(false);
        return Positions[axisIndex];
    }

    private static async Task PumpAsync(IntPtr h, int rounds, CancellationToken token)
    {
        for (int i = 0; i < rounds && !token.IsCancellationRequested; i++)
        {
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);
            try
            {
                await Task.Delay(5, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void OnDispatch(IntPtr pData, uint cbData, IntPtr pContext)
    {
        if (pData == IntPtr.Zero) return;
        if ((uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId)
            != SimConnectApi.RECV_ID_SIMOBJECT_DATA) return;

        // 按 requestID 分派：每个轴一个独立的请求，互不干扰
        uint requestId = (uint)Marshal.ReadInt32(pData, RecvOffsetRequestId);

        if (requestId == TitleRequestId)
        {
            var bytes = new byte[TitleBufferSize];
            Marshal.Copy(pData + SimConnectApi.RecvOffsetSimObjectData, bytes, 0, TitleBufferSize);
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul < 0) nul = bytes.Length;
            string text = System.Text.Encoding.ASCII.GetString(bytes, 0, nul).Trim();
            if (text.Length > 0) _aircraftTitle = text;
            return;
        }

        int axisIndex = (int)(requestId - RequestIdBase);
        if (axisIndex < 0 || axisIndex >= Positions.Length) return;

        int defineCount = Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetDefineCount);

        // 单独订阅之后每包应该只有 1 项。多于 1 项说明定义串了，值得报一次。
        if (defineCount < 1)
        {
            if (!_emptyReported)
            {
                _emptyReported = true;
                Console.WriteLine($"  ! 请求 {requestId} 回包没有数据项 —— 该 SimVar 可能无效");
            }
            return;
        }

        Positions[axisIndex] = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(pData, SimConnectApi.RecvOffsetSimObjectData));
    }

    private static bool _emptyReported;

    /// <summary>当前加载的机型名。</summary>
    private static string _aircraftTitle = string.Empty;
}
