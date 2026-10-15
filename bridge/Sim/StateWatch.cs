using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 状态监视：持续打印飞机当前的关键状态，用来查"自动驾驶/自动油门反复断开"这类问题。
///
/// 设计意图是**在警报正在响的时候看**。断开类警报的触发原因（失速、超速、姿态超限、
/// 操纵输入冲突）都体现在这些变量上，盯着看一遍基本就能定位。
///
/// 这个工具与手机、桥接主程序完全无关，可以单独运行。
/// </summary>
public static class StateWatch
{
    private static readonly (string Name, string Units)[] Fields =
    {
        ("AIRSPEED INDICATED", "Knots"),                    // 0
        ("PLANE ALTITUDE", "Feet"),                         // 1
        ("VERTICAL SPEED", "Feet per minute"),              // 2
        ("PLANE PITCH DEGREES", "Degrees"),                 // 3
        ("PLANE BANK DEGREES", "Degrees"),                  // 4
        ("G FORCE", "GForce"),                              // 5
        ("STALL WARNING", "Bool"),                          // 6
        ("OVERSPEED WARNING", "Bool"),                      // 7
        ("AUTOPILOT MASTER", "Bool"),                       // 8
        ("AUTOTHROTTLE ACTIVE", "Bool"),                    // 9
        ("AUTOPILOT FLIGHT DIRECTOR ACTIVE", "Bool"),       // 10
        ("AUTOPILOT THROTTLE ARM", "Bool"),                 // 11
        ("SIM ON GROUND", "Bool"),                          // 12
        ("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent Over 100"), // 13
        ("FLAPS HANDLE INDEX", "Number"),                   // 14
        ("BRAKE PARKING INDICATOR", "Bool"),                // 15

        // 起落架与离地高度：用来判断是不是 GPWS 类警告
        // （TOO LOW GEAR / SINK RATE 这些在进近时最容易误触发，也和"关不关 AP"无关）
        ("GEAR HANDLE POSITION", "Bool"),                   // 16
        ("PLANE ALT ABOVE GROUND", "Feet"),                 // 17
        ("RADIO HEIGHT", "Feet"),                           // 18

        // FCU 上选定的高度。A320 的高度警报只跟它有关，与 AP 开不开无关 ——
        // 偏离选定高度 ±200 ft 就会响起三音和弦，飞机一路下降就会一路响。
        ("AUTOPILOT ALTITUDE LOCK VAR", "Feet"),            // 19
    };

    private const uint DefineBase = 700;
    private const uint RequestBase = 700;

    private static SimConnectApi.DispatchProc? _dispatch;
    private static readonly double[] V = new double[Fields.Length];

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  飞机状态监视 —— 请在警报响的时候看这个窗口");
        Console.WriteLine("  ─────────────────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            return 1;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr h, "AirYoke State Watch", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败 0x{hr:X8}");
            return 1;
        }

        for (int i = 0; i < Fields.Length; i++)
        {
            SimConnectApi.SimConnect_AddToDataDefinition(
                h, DefineBase + (uint)i, Fields[i].Name, Fields[i].Units,
                SimConnectApi.DATATYPE_FLOAT64, 0f, 0);
            SimConnectApi.SimConnect_RequestDataOnSimObject(
                h, RequestBase + (uint)i, DefineBase + (uint)i, SimConnectApi.OBJECT_ID_USER,
                SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0);
        }

        Console.WriteLine("  已订阅。Ctrl+C 退出。");
        Console.WriteLine();

        bool wasAutopilot = false;
        bool wasAthr = false;
        var lastDumpAt = DateTime.UtcNow;

        while (!token.IsCancellationRequested)
        {
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);

            bool ap = V[8] > 0.5;
            bool athr = V[9] > 0.5;

            // 断开瞬间立刻打印 —— 这是排查断开类问题的核心事件
            if (wasAutopilot && !ap)
            {
                Console.WriteLine();
                Console.WriteLine("  >>> AP 断开了！ <<<");
                DumpState();
            }
            if (wasAthr && !athr)
            {
                Console.WriteLine();
                Console.WriteLine("  >>> A/THR 断开了！ <<<");
                DumpState();
            }
            wasAutopilot = ap;
            wasAthr = athr;

            // 另外每 2 秒打一次常态快照。
            // 有些警报与 AP/A-THR 无关（比如失速、超速、高度警报），
            // 只靠"断开时打印"是看不到的。
            if ((DateTime.UtcNow - lastDumpAt).TotalSeconds >= 2)
            {
                lastDumpAt = DateTime.UtcNow;
                DumpState();
            }

            try
            {
                await Task.Delay(100, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        SimConnectApi.SimConnect_Close(h);
        return 0;
    }

    private static void DumpState()
    {
        Console.WriteLine($"      空速 {V[0]:F1} kt   高度 {V[1]:F0} ft   升降率 {V[2]:F0} fpm");
        Console.WriteLine($"      俯仰 {V[3]:F1}°   坡度 {V[4]:F1}°   过载 {V[5]:F2} G");
        Console.WriteLine($"      失速警告 {(V[6] > 0.5 ? "★是★" : "否")}   " +
                          $"超速警告 {(V[7] > 0.5 ? "★是★" : "否")}   " +
                          $"在地面 {(V[12] > 0.5 ? "是" : "否")}");
        Console.WriteLine($"      AP {(V[8] > 0.5 ? "接通" : "断开")}   " +
                          $"A/THR {(V[9] > 0.5 ? "接通" : "断开")}   " +
                          $"A/THR预位 {(V[11] > 0.5 ? "是" : "否")}   " +
                          $"FD {(V[10] > 0.5 ? "开" : "关")}");
        Console.WriteLine($"      推力杆 {V[13]:F3}   襟翼 {V[14]:F0}   停留刹车 {(V[15] > 0.5 ? "是" : "否")}");
        Console.WriteLine($"      起落架手柄 {(V[16] > 0.5 ? "放下" : "★收起★")}   " +
                          $"离地 {V[17]:F0} ft   无线电高度 {V[18]:F0} ft");

        double selectedAlt = V[19];
        double delta = V[1] - selectedAlt;
        Console.WriteLine($"      FCU 选定高度 {selectedAlt:F0} ft   " +
                          $"与当前相差 {delta:+0;-0;0} ft" +
                          (Math.Abs(delta) > 200 ? "   ★超过 200ft —— 高度警报会持续响★" : ""));
        Console.WriteLine();
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
