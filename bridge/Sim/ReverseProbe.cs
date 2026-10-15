using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 反推实测：逐个试候选事件，看哪一个能真的让 A320 进入反推。
///
/// ## 为什么必须实测
///
/// 当初标定油门轴时测的是 <c>AXIS_THROTTLE_SET</c>，结论是"−16383..+16383
/// 线性对应手柄 0..1，整条轴上没有反推区间"。这个结论本身没错，但它**只对那一个事件成立**。
///
/// 论坛上普遍反映 A320 的反推要走**逐台发动机的 `THROTTLE1_SET` / `THROTTLE2_SET`**，
/// 负值区才是反推。但这类说法在不同机型、不同版本上表现不一，
/// 官方 SDK 也没有一份"哪个机型支持哪个事件"的对照表 —— 所以不猜，直接测。
///
/// ## 运行前提
///
/// **必须在地面、发动机运转、油门在慢车。** 程序会先自检并在条件不满足时拒绝继续，
/// 免得在空中把推力杆推到反推区（真机上那是要命的操作）。
/// </summary>
public static class ReverseProbe
{
    // ---- 读回的状态 ----
    private static readonly (string Name, string Units)[] Fields =
    {
        ("SIM ON GROUND", "Bool"),                                      // 0
        ("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent Over 100"),  // 1
        ("GENERAL ENG THROTTLE LEVER POSITION:2", "Percent Over 100"),  // 2
        ("TURB ENG N1:1", "Percent"),                                   // 3
        ("TURB ENG REVERSE NOZZLE PERCENT:1", "Percent"),               // 4
        ("TURB ENG REVERSE NOZZLE PERCENT:2", "Percent"),               // 5
    };

    private const int FieldOnGround = 0;
    private const int FieldLever1 = 1;
    private const int FieldLever2 = 2;
    private const int FieldN1 = 3;
    private const int FieldReverser1 = 4;

    private const uint DefineBase = 800;
    private const uint RequestBase = 800;

    private const uint EventBase = 100;

    /// <summary>复位用的"收油门到慢车"事件 ID。</summary>
    private const uint EventThrottleCut = 90;

    /// <summary>全反推对应的轴值。SimConnect 的轴量满量程是 ±16383。</summary>
    private const int FullReverse = -16383;

    /// <summary>
    /// 油门轴扫描点。
    ///
    /// ## 这一步要回答什么
    ///
    /// **"慢车"到底在轴的哪个位置。**
    ///
    /// 当初标定油门轴时，判据是 `GENERAL ENG THROTTLE LEVER POSITION`，结论是
    /// "−16383 = 慢车、+16383 = 满推力，整条轴上没有反推区间"，于是桥接程序里
    /// 写了 `Math.Clamp(normalized, 0f, 1f)` 把负值一律夹成慢车。
    ///
    /// **但这个判据本身有个致命缺陷：手柄位置读数在 0 处会被夹住，负数根本显示不出来。**
    /// 如果慢车其实在轴中点（data=0）、负半轴就是反推区，那么"−16383 读到 0"
    /// 这个观测和"−16383 是慢车"看起来一模一样 —— 两者无法用手柄位置区分。
    ///
    /// 所以改用 **N1** 做判据：它是唯一不会被限幅的读数。
    /// 扫描后看 N1 的**最低点**落在哪个 data 值上，那就是慢车；中点两侧就是反推与正推。
    /// </summary>
    private static readonly int[] SweepPoints =
    {
        -16383, -14000, -12000, -10000, -8000, -6000, -4000, -2000,
        0,
        4000, 8000, 12000, 16383,
    };

    private static SimConnectApi.DispatchProc? _dispatch;
    private static readonly double[] V = new double[Fields.Length];

    /// <summary>
    /// 施加事件过程中的**峰值**。
    ///
    /// 为什么必须记峰值而不是读末值：人工基准那轮清楚地显示，反推装置会冲到
    /// 11.89% 然后**自己退回** 0.05%。只读一个时间点的值，采到的多半是退回去之后的状态 ——
    /// 上一轮就是这么把"有效"判成"无反应"的。
    /// </summary>
    private static double _peakReverser;
    private static double _peakN1;

    private static void ResetPeaks()
    {
        _peakReverser = 0;
        _peakN1 = V[FieldN1];
    }

    private static void TrackPeak()
    {
        if (V[FieldReverser1] > _peakReverser) _peakReverser = V[FieldReverser1];
        if (V[FieldN1] > _peakN1) _peakN1 = V[FieldN1];
    }

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  反推实测 —— 会真的推油门，只能在地面做");
        Console.WriteLine("  ─────────────────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            return 1;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr h, "AirYoke Reverse Probe", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败 0x{hr:X8}");
            return 1;
        }

        for (int i = 0; i < Fields.Length; i++)
        {
            int addHr = SimConnectApi.SimConnect_AddToDataDefinition(
                h, DefineBase + (uint)i, Fields[i].Name, Fields[i].Units,
                SimConnectApi.DATATYPE_FLOAT64, 0f, 0);
            if (!SimConnectApi.Succeeded(addHr))
            {
                // 无效的 SimVar 名不该静默吞掉 —— 那会让人把"读不到"误判成"没反应"
                Console.WriteLine($"  ⚠ 订阅失败：{Fields[i].Name}  (0x{addHr:X8})");
            }
            SimConnectApi.SimConnect_RequestDataOnSimObject(
                h, RequestBase + (uint)i, DefineBase + (uint)i, SimConnectApi.OBJECT_ID_USER,
                SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0);
        }

        // 只注册两个事件：被扫描的油门轴，和复位用的收油门
        const uint eventAxis = EventBase;
        SimConnectApi.SimConnect_MapClientEventToSimEvent(h, eventAxis, "AXIS_THROTTLE_SET");
        SimConnectApi.SimConnect_MapClientEventToSimEvent(h, EventThrottleCut, "THROTTLE_CUT");

        await Settle(h, 1500, token).ConfigureAwait(false);

        // ---- 安全检查：不在地面就拒绝继续 ----
        if (V[FieldOnGround] < 0.5)
        {
            Console.WriteLine();
            Console.WriteLine("  ★ 飞机不在空中/地面的判定是「不在 地面」——已中止。");
            Console.WriteLine("    反推只能在**地面**测试。请落地后再运行。");
            SimConnectApi.SimConnect_Close(h);
            return 1;
        }

        Console.WriteLine($"  地面确认 ✓   慢车 N1 = {V[FieldN1]:F1}%");
        Console.WriteLine();

        // ---- 人工基准 ----
        //
        // 先让用户用游戏**自己的**操作进一次反推。这一步的价值在于给出"能用的样子"：
        // 反推到底能不能进、进了之后手柄和 N1 读数是什样。有了这个基准，
        // 后面逐个试候选事件时才知道该拿什么去比 —— 否则连"成功"长什么样都不知道。
        Console.WriteLine("  第 1 步（人工基准）：请你用**游戏自己的操作**试着进反推");
        Console.WriteLine("     · 按住 F2 不放（默认键位，到底后进入反推），或");
        Console.WriteLine("     · 用鼠标把推力手柄拖过慢车卡位继续往下");
        Console.WriteLine("  有 25 秒。下面是实时读数，请盯着看 N1 有没有跳起来：");
        Console.WriteLine();

        long manualDeadline = Environment.TickCount64 + 60_000;
        while (Environment.TickCount64 < manualDeadline && !token.IsCancellationRequested)
        {
            TrackPeak();
            Console.Write($"\r    手柄 {V[FieldLever1]:F3}    N1 {V[FieldN1]:F1}%    " +
                          $"反推装置 {V[FieldReverser1]:F2}%   (峰值 {_peakReverser:F2}%)          ");
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);
            try
            {
                await Task.Delay(250, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"  ▣ 人工基准结果：反推装置峰值 {_peakReverser:F2}%   " +
                          $"N1 峰值 {_peakN1:F1}%（慢车 {V[FieldN1]:F1}%）");
        Console.WriteLine(_peakReverser > 1.0
            ? "     → 反推确实进去过。这就是后面要比对的基准。"
            : "     → 全程没看到反推。那说明这台飞机比不了，得换个思路。");
        Console.WriteLine();
        Console.WriteLine("  第 2 步：扫描油门轴 —— 找「慢车」到底在轴的哪个位置");
        Console.WriteLine("  每个点：回慢车 → 持续下发 2 秒 → 读 N1");
        Console.WriteLine();

        double minN1 = double.MaxValue;
        int minAt = 0;

        foreach (int value in SweepPoints)
        {
            await ResetToIdle(h, token).ConfigureAwait(false);
            await Apply(h, eventAxis, unchecked((uint)value), 40, token).ConfigureAwait(false);

            double n1 = V[FieldN1];
            if (n1 < minN1)
            {
                minN1 = n1;
                minAt = value;
            }

            Console.WriteLine($"    data {value,7}   N1 {n1,5:F1}%   " +
                              $"手柄 {V[FieldLever1]:F3}   反推装置 {V[FieldReverser1]:F2}%");
        }

        Console.WriteLine();
        Console.WriteLine($"  ▣ N1 最低点出现在 data = {minAt}（N1 {minN1:F1}%）");
        Console.WriteLine(minAt < -2000
            ? "     → 慢车在轴中点附近，负半轴就是反推区。桥接程序的 Math.Clamp 把反推夹掉了。"
            : minAt > -2000 && minAt < 2000
                ? "     → 慢车在 data=0 —— 负半轴是反推区。桥接程序的 Math.Clamp 把反推夹掉了。"
                : "     → 慢车在最负端，这条轴上确实没有反推区间。");
        Console.WriteLine();

        // 扫描到全反推后停在最负端不好，收回慢车
        await ResetToIdle(h, token).ConfigureAwait(false);
        Console.WriteLine("  已把油门收回慢车。");
        SimConnectApi.SimConnect_Close(h);
        return 0;
    }

    /// <summary>
    /// 按 <paramref name="repeat"/> 次下发，每次间隔 50ms 并泵消息。
    ///
    /// 这正是桥接程序 50Hz 持续流的真实节奏 —— 第一轮每个候选只发一次，
    /// 测出来的现象与真实使用完全不同，那次的结论不能用。
    /// </summary>
    private static async Task Apply(IntPtr h, uint eventId, uint data, int repeat, CancellationToken token)
    {
        for (int i = 0; i < repeat; i++)
        {
            Transmit(h, eventId, data);
            await Settle(h, 50, token).ConfigureAwait(false);
        }
        // 停手后再等一会儿，让状态稳定下来再读
        await Settle(h, 1200, token).ConfigureAwait(false);
    }

    private static async Task ResetToIdle(IntPtr h, CancellationToken token)
    {
        // 上一轮试过 THROTTLE{n}_SET 回慢车，在该机型上是空操作。
        // 改用离散的"收油门"事件，多发几次确保生效。
        for (int i = 0; i < 5; i++)
        {
            Transmit(h, EventThrottleCut, 0);
            await Settle(h, 60, token).ConfigureAwait(false);
        }
        await Settle(h, 1500, token).ConfigureAwait(false);
    }

    private static void Transmit(IntPtr h, uint eventId, uint data)
    {
        SimConnectApi.SimConnect_TransmitClientEvent(
            h, SimConnectApi.OBJECT_ID_USER, eventId, data, 0,
            SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);
    }

    /// <summary>一边泵消息一边等，否则读回的值永远是旧的。</summary>
    private static async Task Settle(IntPtr h, int ms, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline && !token.IsCancellationRequested)
        {
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);
            TrackPeak();
            try
            {
                await Task.Delay(50, token).ConfigureAwait(false);
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

        uint requestId = (uint)Marshal.ReadInt32(pData, 12);
        int index = (int)(requestId - RequestBase);
        if (index < 0 || index >= V.Length) return;

        V[index] = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(pData, SimConnectApi.RecvOffsetSimObjectData));
    }
}
