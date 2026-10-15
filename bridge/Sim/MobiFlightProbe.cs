using System.Runtime.InteropServices;
using System.Text;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// MobiFlight WASM 模块探针：验证模块可用，并列出 A320 的襟翼/减速板 LVar。
///
/// ## 为什么要绕这么一圈
///
/// A320 的襟翼与减速板是**自定义逻辑**，标准 SimConnect 事件一个都不响应
/// （标定阶段试过 22 个，全部无效）。要动它们只能直接读写飞机的 LVar ——
/// 而 LVar 在常规 SimConnect 接口里访问不到，必须由 WASM 模块代为执行计算器脚本。
///
/// ## 实现依据
///
/// 所有调用参数都照抄官方推荐的第三方参考实现
/// （MSFSPythonSimConnectMobiFlightExtension/prototype/mobiflight_variable_requests.py），
/// 而不是照着 README 猜。有几处 README 没写清、或者写法不同就会直接报错的地方：
///
///   · <c>AddToClientDataDefinition</c> 的 DatumId 必须传 <c>SIMCONNECT_UNUSED</c>
///     （0xFFFFFFFF），传 0 会被拒（SimConnect 异常 0x1F）
///   · 字符串数据区按 1024 字节开，不是 128
///   · <c>RequestClientData</c> 的 flags 用 1（仅变化时通知），不是 0
///   · 客户端数据区 ID 用 0..5 这种小数字
/// </summary>
public static class MobiFlightProbe
{
    /// <summary>我们自己注册的客户端名。模块会为它建 AirYoke.Lvars/.Command/.Response。</summary>
    private const string ClientName = "AirYoke";

    /// <summary>
    /// SimConnect 里"未使用"的哨兵值。
    /// <c>AddToClientDataDefinition</c> 的 DatumId 必须传它。
    /// </summary>
    private const uint SimConnectUnused = 0xFFFFFFFF;

    // ---- 客户端数据区 ID ----
    private const uint CdaDefaultLvars = 0;
    private const uint CdaDefaultCommand = 1;
    private const uint CdaDefaultResponse = 2;
    private const uint CdaLvars = 3;
    private const uint CdaCommand = 4;
    private const uint CdaResponse = 5;

    // ---- 数据定义 ID（参考实现用 0 和 1）----
    private const uint DefDefault = 0;
    private const uint DefOwn = 1;

    /// <summary>请求标志：仅当值变化时通知。</summary>
    private const uint FlagChanged = 1;

    /// <summary>命令/响应字符串区的字节数。参考实现用 1024。</summary>
    private const int StringSize = 1024;

    /// <summary>自定义 LVar 在 LVars 区里的起始偏移（参考实现从 1000 开始编号）。</summary>
    private const int SimVarDefOffset = 1000;

    private static SimConnectApi.DispatchProc? _dispatch;
    private static readonly List<string> Responses = new();

    public static async Task<int> RunAsync(CancellationToken token)
    {
        Console.WriteLine();
        Console.WriteLine("  MobiFlight WASM 模块探针");
        Console.WriteLine("  ─────────────────────────────────────────────────────");

        if (!SimConnectApi.EnsureLoaded())
        {
            Console.Error.WriteLine("  没找到 SimConnect.dll。");
            return 1;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr h, "AirYoke MobiFlight Probe", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr))
        {
            Console.Error.WriteLine($"  SimConnect_Open 失败 0x{hr:X8}");
            return 1;
        }

        // ---- 1. 接上默认通道 ----
        Map(h, "MobiFlight.Lvars", CdaDefaultLvars);
        bool mapped = Map(h, "MobiFlight.Command", CdaDefaultCommand)
                      && Map(h, "MobiFlight.Response", CdaDefaultResponse);

        if (!mapped)
        {
            Console.WriteLine();
            Console.WriteLine("  ★ 连不上 MobiFlight 的默认通道 —— 模块没被模拟器加载。请检查：");
            Console.WriteLine("      · 游戏是否在装好模块之后**重启过**（社区包只在启动时读）");
            Console.WriteLine("      · 市场 → 我的内容 → 已安装 → 确认 mobiflight-event-module 处于**已启用**");
            SimConnectApi.SimConnect_Close(h);
            return 1;
        }

        Define(h, DefDefault, StringSize);
        Subscribe(h, CdaDefaultResponse, DefDefault);

        // 文档提醒：客户端刚启动后的第一条命令有时会被丢掉，所以先发一条无意义的。
        await Settle(h, 700, token).ConfigureAwait(false);
        SendCommand(h, CdaDefaultCommand, DefDefault, "Do Nothing");
        await Settle(h, 500, token).ConfigureAwait(false);

        DrainResponses();
        SendCommand(h, CdaDefaultCommand, DefDefault, $"MF.Clients.Add.{ClientName}");
        await Settle(h, 2500, token).ConfigureAwait(false);

        var addReplies = DrainResponses();
        if (!addReplies.Any(r => r.Contains(ClientName, StringComparison.Ordinal)))
        {
            Console.WriteLine();
            Console.WriteLine("  ★ 注册专属通道没有回应。收到的全部回应：");
            if (addReplies.Count == 0) Console.WriteLine("      （一条都没有）");
            foreach (var r in addReplies) Console.WriteLine($"      「{r}」");
            SimConnectApi.SimConnect_Close(h);
            return 1;
        }

        Console.WriteLine($"  模块在线，专属通道已建立 ✓");
        foreach (var r in addReplies) Console.WriteLine($"      回应：「{r}」");
        Console.WriteLine();

        // ---- 2. 接上我们自己的通道 ----
        Map(h, $"{ClientName}.Lvars", CdaLvars);
        Map(h, $"{ClientName}.Command", CdaCommand);
        Map(h, $"{ClientName}.Response", CdaResponse);

        Define(h, DefOwn, StringSize);
        Subscribe(h, CdaResponse, DefOwn);

        await Settle(h, 500, token).ConfigureAwait(false);
        DrainResponses();

        // ---- 3. 列出全部 LVar ----
        SendCommand(h, CdaCommand, DefOwn, "MF.LVars.List");
        await Settle(h, 5000, token).ConfigureAwait(false);

        var listReplies = DrainResponses();

        var names = new List<string>();
        bool inList = false;
        foreach (var r in listReplies)
        {
            if (r.Contains("MF.LVars.List.Start", StringComparison.Ordinal)) { inList = true; continue; }
            if (r.Contains("MF.LVars.List.End", StringComparison.Ordinal)) { inList = false; continue; }
            if (inList && r.Length > 0) names.Add(r);
        }

        if (names.Count == 0)
        {
            Console.WriteLine("  ★ LVar 列表是空的。收到的回包（最多 20 条）：");
            foreach (var r in listReplies.Take(20)) Console.WriteLine($"      「{r}」");
            SimConnectApi.SimConnect_Close(h);
            return 1;
        }

        Console.WriteLine($"  飞机共暴露 {names.Count} 个 LVar。");
        Console.WriteLine();

        // ---- 4. 挑出跟襟翼/减速板有关的 ----
        string[] keywords = { "FLAP", "SPOIL", "SPLR", "SPEEDBRAKE", "SPEED_BRAKE", "SLAT" };
        var interesting = names
            .Where(n => keywords.Any(k => n.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Console.WriteLine($"  其中与襟翼/减速板相关的一共 {interesting.Count} 个：");
        Console.WriteLine();
        foreach (var n in interesting) Console.WriteLine($"      {n}");
        Console.WriteLine();

        SimConnectApi.SimConnect_Close(h);
        return 0;
    }

    /// <summary>
    /// 每一步都打印返回码。
    ///
    /// 这个协议最容易坑人的地方就在这里：<c>MapClientDataNameToID</c> 即使名字不存在
    /// 也可能返回成功，于是"通道映射成功"并不能证明模块在跑。不给返回码就只能靠猜。
    /// </summary>
    private static bool Map(IntPtr h, string name, uint id)
    {
        int hr = SimConnectApi.SimConnect_MapClientDataNameToID(h, name, id);
        Console.WriteLine($"      Map  {name,-28} → 0x{hr:X8}{(SimConnectApi.Succeeded(hr) ? "" : "  ✗")}");
        return SimConnectApi.Succeeded(hr);
    }

    private static void Define(IntPtr h, uint defineId, int size)
    {
        int hr = SimConnectApi.SimConnect_AddToClientDataDefinition(
            h, defineId, 0, (uint)size, 0f, SimConnectUnused);
        Console.WriteLine($"      Define {defineId} ({size} 字节) → 0x{hr:X8}" +
                          (SimConnectApi.Succeeded(hr) ? "" : "  ✗"));
    }

    private static void Subscribe(IntPtr h, uint cda, uint defineId)
    {
        int hr = SimConnectApi.SimConnect_RequestClientData(
            h, cda, defineId, defineId,
            SimConnectApi.CLIENT_DATA_PERIOD_ON_SET, FlagChanged, 0, 0, 0);
        Console.WriteLine($"      Subscribe 数据区 {cda} → 0x{hr:X8}" +
                          (SimConnectApi.Succeeded(hr) ? "" : "  ✗"));
    }

    /// <summary>把一条命令写进命令通道。字符串按 ASCII 补零到定长。</summary>
    private static void SendCommand(IntPtr h, uint cda, uint def, string command)
    {
        var buffer = new byte[StringSize];
        var text = Encoding.ASCII.GetBytes(command);
        Array.Copy(text, buffer, Math.Min(text.Length, StringSize - 1));

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            SimConnectApi.SimConnect_SetClientData(
                h, cda, def, 0, 0, StringSize, handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    private static List<string> DrainResponses()
    {
        lock (Responses)
        {
            var copy = new List<string>(Responses);
            Responses.Clear();
            return copy;
        }
    }

    private static async Task Settle(IntPtr h, int ms, CancellationToken token)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline && !token.IsCancellationRequested)
        {
            SimConnectApi.SimConnect_CallDispatch(h, _dispatch!, IntPtr.Zero);
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

        uint id = (uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId);

        if (id == SimConnectApi.RECV_ID_EXCEPTION)
        {
            uint code = (uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetException);
            Console.WriteLine($"  ⚠ SimConnect 异常 0x{code:X8}");
            return;
        }

        if (id != SimConnectApi.RECV_ID_CLIENT_DATA) return;

        // 客户端数据区的接收结构与 SIMOBJECT_DATA 同形，沿用同一套偏移
        string text = Marshal.PtrToStringAnsi(
            IntPtr.Add(pData, SimConnectApi.RecvOffsetSimObjectData)) ?? string.Empty;

        int nul = text.IndexOf('\0');
        if (nul >= 0) text = text[..nul];
        text = text.Trim();
        if (text.Length == 0) return;

        lock (Responses)
        {
            Responses.Add(text);
        }
    }
}
