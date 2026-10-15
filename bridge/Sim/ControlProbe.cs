using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 实测襟翼 / 减速板的事件名与回读变量，并找出真正能反映"实际推力"的 SimVar。
///
/// 和前两个探针一样：**不查文档，直接问模拟器**。
/// `SimConnect_MapClientEventToSimEvent` 对无效事件名不报错，
/// 只在发送时静默失效——只有实测才能分辨。
///
/// 油门部分要解决的是一个具体问题：A320 开启自动油门后，
/// `GENERAL ENG THROTTLE LEVER POSITION` 反映的是"推力杆位置"，
/// 而我们需要知道"自动油门把推力设成了多少"。这两者未必是同一个变量。
/// </summary>
public static class ControlProbe
{
    private const uint DefineBase = 400;
    private const uint RequestBase = 400;

    /// <summary>回读变量。顺序即索引。</summary>
    private static readonly (string Name, string Units)[] ReadBacks =
    {
        ("FLAPS HANDLE INDEX", "Number"),                    // 0
        ("FLAPS HANDLE PERCENT", "Percent Over 100"),        // 1
        ("SPOILERS HANDLE POSITION", "Percent Over 100"),    // 2
        ("SPOILERS ARMED", "Bool"),                          // 3
        ("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent Over 100"), // 4
        ("TURB ENG N1:1", "Percent"),                        // 5
        ("AUTOPILOT THROTTLE ARM", "Bool"),                  // 6
        ("AUTOTHROTTLE ACTIVE", "Bool"),                     // 7

        // 飞机是否"活着"：襟翼/减速板要液压和电才能动，
        // 这两个变量用来区分"事件名不对"和"系统没通电"。
        ("ELECTRICAL MASTER BATTERY", "Bool"),               // 8
        ("GENERAL ENG COMBUSTION:1", "Bool"),                // 9
        ("SIM ON GROUND", "Bool"),                           // 10
        ("BRAKE PARKING INDICATOR", "Bool"),                 // 11

        // 交叉验证用：如果事件真的让襟翼动了，这两个"实际翼面位置"必然跟着变。
        // 只看 FLAPS HANDLE INDEX 的话，万一那个变量在这架飞机上不更新，
        // 就会把"能控制"误判成"不能控制"。
        ("TRAILING EDGE FLAPS LEFT PERCENT", "Percent Over 100"),   // 12
        ("TRAILING EDGE FLAPS RIGHT PERCENT", "Percent Over 100"),  // 13
    };

    private const int FlapsIndex = 0;
    private const int FlapsPercent = 1;
    private const int SpoilerPos = 2;
    private const int SpoilerArmed = 3;
    private const int ThrottleLever = 4;
    private const int N1 = 5;
    private const int AthrArm = 6;
    private const int AthrActive = 7;

    private static SimConnectApi.DispatchProc? _dispatch;
    private static readonly double[] Values = new double[ReadBacks.Length];

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  襟翼 / 减速板 / 油门回读 实测");
        Console.WriteLine("  ─────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            return 1;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr h, "AirYoke Control Probe", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败 0x{hr:X8}");
            return 1;
        }

        Console.WriteLine("  订阅回读变量：");
        for (int i = 0; i < ReadBacks.Length; i++)
        {
            SimConnectApi.SimConnect_AddToDataDefinition(
                h, DefineBase + (uint)i, ReadBacks[i].Name, ReadBacks[i].Units,
                SimConnectApi.DATATYPE_FLOAT64, 0f, 0);
            SimConnectApi.SimConnect_RequestDataOnSimObject(
                h, RequestBase + (uint)i, DefineBase + (uint)i, SimConnectApi.OBJECT_ID_USER,
                SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0);
            Console.WriteLine($"      {ReadBacks[i].Name}");
        }

        // 机型
        SimConnectApi.SimConnect_AddToDataDefinition(
            h, 500, "TITLE", null, SimConnectApi.DATATYPE_STRING256, 0f, 0);
        SimConnectApi.SimConnect_RequestDataOnSimObject(
            h, 500, 500, SimConnectApi.OBJECT_ID_USER, SimConnectApi.PERIOD_SECOND, 0, 0, 0, 0);

        await PumpAsync(h, 80, token).ConfigureAwait(false);

        PrintBaseline();

        await TestFlapsAsync(h, token).ConfigureAwait(false);
        await TestSpoilersAsync(h, token).ConfigureAwait(false);
        await TestThrottleReadbackAsync(h, token).ConfigureAwait(false);

        SimConnectApi.SimConnect_Close(h);
        return 0;
    }

    private static void PrintBaseline()
    {
        Console.WriteLine();
        Console.WriteLine($"  当前机型：{_title}");
        Console.WriteLine("  基线读数：");
        PrintValues("      ");
    }

    private static void PrintValues(string indent)
    {
        Console.WriteLine($"{indent}襟翼 索引={Values[FlapsIndex]:F0}  百分比={Values[FlapsPercent]:F3}");
        Console.WriteLine($"{indent}减速板 位置={Values[SpoilerPos]:F3}  预位={Values[SpoilerArmed]:F0}");
        Console.WriteLine($"{indent}推力杆={Values[ThrottleLever]:F3}  N1={Values[N1]:F1}%  " +
                          $"A/THR 预位={Values[AthrArm]:F0}  接通={Values[AthrActive]:F0}");
        Console.WriteLine($"{indent}【飞机状态】电瓶={Values[8]:F0}  发动机1运转={Values[9]:F0}  " +
                          $"在地面={Values[10]:F0}  停留刹车={Values[11]:F0}");
    }

    // ---------------- 襟翼 ----------------

    private static async Task TestFlapsAsync(IntPtr h, CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  ══ 襟翼 ══");

        // 先试通用事件，再试"档位专用"事件 —— 高精度机模常常只认后者
        string[] candidates =
        {
            "FLAPS_UP", "FLAPS_DOWN", "FLAPS_INCR", "FLAPS_DECR", "FLAPS_SET",
            "FLAPS_1", "FLAPS_2", "FLAPS_3", "FLAPS_4",
            "AXIS_FLAPS_SET", "FLAPS_HANDLE_SET", "FLAP_DETENTS_SET",
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            uint id = (uint)(600 + i);
            if (!SimConnectApi.Succeeded(
                    SimConnectApi.SimConnect_MapClientEventToSimEvent(h, id, candidates[i])))
            {
                Console.WriteLine($"      {candidates[i],-16} 映射失败");
                continue;
            }

            double before = Values[FlapsIndex];

            // 连发三次：有些机型的档位是"一步一档"，按一次看不出变化
            for (int rep = 0; rep < 3; rep++)
            {
                uint data = candidates[i] == "FLAPS_SET" ? (uint)(8191 - rep * 4095) : 0u;
                SimConnectApi.SimConnect_TransmitClientEvent(
                    h, SimConnectApi.OBJECT_ID_USER, id, data, 0,
                    SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);
                await PumpAsync(h, 10, token).ConfigureAwait(false);
            }

            await PumpAsync(h, 25, token).ConfigureAwait(false);
            double after = Values[FlapsIndex];
            double surface = Values[12]; // 后缘襟翼实际位置，交叉验证用

            bool moved = Math.Abs(after - before) > 0.01;
            Console.WriteLine(
                $"      {candidates[i],-16} 手柄 {before:F0} -> {after:F0}   " +
                $"翼面 {surface:F3}   " + (moved ? "✓ 有效" : "无效"));

            await Task.Delay(200, token).ConfigureAwait(false);
        }

        Console.WriteLine("      收尾：收襟翼");
        uint upId = 700;
        SimConnectApi.SimConnect_MapClientEventToSimEvent(h, upId, "FLAPS_UP");
        for (int i = 0; i < 6; i++)
        {
            SimConnectApi.SimConnect_TransmitClientEvent(
                h, SimConnectApi.OBJECT_ID_USER, upId, 0, 0,
                SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);
            await PumpAsync(h, 15, token).ConfigureAwait(false);
        }
        Console.WriteLine($"      收完档位 = {Values[FlapsIndex]:F0}");
    }

    // ---------------- 减速板 ----------------

    private static async Task TestSpoilersAsync(IntPtr h, CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  ══ 减速板 ══");

        string[] candidates =
        {
            "SPOILERS_TOGGLE", "SPOILERS_SET", "SPOILERS_ARM_TOGGLE", "SPOILERS_ON", "SPOILERS_OFF",
            "SPOILER_SET", "AXIS_SPOILER_SET", "SPOILERS_ARM_SET", "SPOILERS_ARM_ON", "SPOILERS_ARM_OFF",
        };

        for (int i = 0; i < candidates.Length; i++)
        {
            uint id = (uint)(610 + i);
            if (!SimConnectApi.Succeeded(
                    SimConnectApi.SimConnect_MapClientEventToSimEvent(h, id, candidates[i])))
            {
                Console.WriteLine($"      {candidates[i],-20} 映射失败");
                continue;
            }

            double before = Values[SpoilerPos];
            double armedBefore = Values[SpoilerArmed];

            uint data = candidates[i] == "SPOILERS_SET" ? 12287u : 0u;
            SimConnectApi.SimConnect_TransmitClientEvent(
                h, SimConnectApi.OBJECT_ID_USER, id, data, 0,
                SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);

            await PumpAsync(h, 25, token).ConfigureAwait(false);

            double after = Values[SpoilerPos];
            double armedAfter = Values[SpoilerArmed];

            bool changed = Math.Abs(after - before) > 0.01 || Math.Abs(armedAfter - armedBefore) > 0.01;
            Console.WriteLine($"      {candidates[i],-20} 位置 {before:F3} -> {after:F3}  预位 {armedBefore:F0} -> {armedAfter:F0}  " +
                              (changed ? "✓ 有效" : "无效"));

            await Task.Delay(200, token).ConfigureAwait(false);
        }
    }

    // ---------------- 油门回读 ----------------

    private static async Task TestThrottleReadbackAsync(IntPtr h, CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  ══ 油门回读：哪个变量真正反映推力 ══");
        Console.WriteLine();
        Console.WriteLine("      发 AXIS_THROTTLE_SET 一个值，看各变量怎么变：");
        Console.WriteLine("      轴值     推力杆     N1      A/THR接通");

        uint id = 620;
        SimConnectApi.SimConnect_MapClientEventToSimEvent(h, id, "AXIS_THROTTLE_SET");

        int[] probes = { 16383, 12000, 8191, 4095, 0, -8191, -16383 };
        foreach (int v in probes)
        {
            SimConnectApi.SimConnect_TransmitClientEvent(
                h, SimConnectApi.OBJECT_ID_USER, id, unchecked((uint)v), 0,
                SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);

            await PumpAsync(h, 30, token).ConfigureAwait(false);

            Console.WriteLine($"      {v,7}  {Values[ThrottleLever],8:F3}  {Values[N1],7:F1}  " +
                              $"{Values[AthrActive],6:F0}");
        }

        // 归到慢车
        SimConnectApi.SimConnect_TransmitClientEvent(
            h, SimConnectApi.OBJECT_ID_USER, id, 0, 0,
            SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);
        await PumpAsync(h, 20, token).ConfigureAwait(false);
    }

    // ---------------- 泵 ----------------

    private static async Task PumpAsync(IntPtr h, int rounds, CancellationToken token)
    {
        for (int i = 0; i < rounds && !token.IsCancellationRequested; i++)
        {
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);
            try
            {
                await Task.Delay(8, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static string _title = "（未获取到）";

    private static void OnDispatch(IntPtr pData, uint cbData, IntPtr pContext)
    {
        if (pData == IntPtr.Zero) return;
        if ((uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId)
            != SimConnectApi.RECV_ID_SIMOBJECT_DATA) return;

        uint requestId = (uint)Marshal.ReadInt32(pData, 12);

        if (requestId == 500)
        {
            var bytes = new byte[256];
            Marshal.Copy(pData + SimConnectApi.RecvOffsetSimObjectData, bytes, 0, 256);
            int nul = Array.IndexOf(bytes, (byte)0);
            if (nul < 0) nul = bytes.Length;
            string t = System.Text.Encoding.ASCII.GetString(bytes, 0, nul).Trim();
            if (t.Length > 0) _title = t;
            return;
        }

        int index = (int)(requestId - RequestBase);
        if (index < 0 || index >= Values.Length) return;

        Values[index] = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(pData, SimConnectApi.RecvOffsetSimObjectData));
    }
}
