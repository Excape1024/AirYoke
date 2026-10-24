using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 微软模拟飞行 SimConnect 原生 C API 的 P/Invoke 封装。
///
/// ## 为什么直接 P/Invoke 原生 DLL
///
/// 官方托管的 <c>Microsoft.FlightSimulator.SimConnect.dll</c> 是老 .NET Framework 4.x 程序集，
/// 在现代 .NET 上引用会有兼容问题，而且要额外装 SDK 才能拿到。
/// 而**原生 <c>SimConnect.dll</c> 随模拟器一起安装**，导出的是标准 C 函数，
/// P/Invoke 直接可用 —— 少一层依赖，也少一个分发负担。
///
/// ## 无窗口模式
///
/// 传统 SimConnect 客户端需要一个窗口句柄来做消息泵。本程序是控制台程序，没有窗口，
/// 所以 `SimConnect_Open` 传 `hWnd = NULL`，改由主循环周期性调用
/// <see cref="SimConnect_CallDispatch"/> 主动收取消息。
/// </summary>
public static class SimConnectApi
{
    public const string DllName = "SimConnect.dll";

    private static bool _resolverInstalled;
    private static string? _resolvedPath;

    /// <summary>已找到并绑定的 SimConnect.dll 路径。</summary>
    public static string? ResolvedPath => _resolvedPath;

    /// <summary>
    /// 定位并绑定 SimConnect.dll。必须在任何 P/Invoke 之前调用一次。
    /// </summary>
    /// <returns>成功返回 true；找不到 DLL 返回 false。</returns>
    public static bool EnsureLoaded()
    {
        if (_resolverInstalled) return _resolvedPath is not null;

        var path = SimConnectLocator.Find();
        if (path is null) return false;

        _resolvedPath = path;

        // 一个程序集只能设一次解析器；重复设置会抛异常，所以上面用 _resolverInstalled 挡住
        NativeLibrary.SetDllImportResolver(typeof(SimConnectApi).Assembly, (name, _, _) =>
        {
            bool wanted = name.Equals(DllName, StringComparison.OrdinalIgnoreCase)
                          || name.Equals("SimConnect", StringComparison.OrdinalIgnoreCase);
            return wanted ? NativeLibrary.Load(path) : IntPtr.Zero;
        });

        _resolverInstalled = true;
        return true;
    }

    // ---------------- 接收消息的 ID ----------------

    public const uint RECV_ID_NULL = 0;
    public const uint RECV_ID_EXCEPTION = 1;
    public const uint RECV_ID_OPEN = 2;
    public const uint RECV_ID_QUIT = 3;
    public const uint RECV_ID_EVENT = 4;
    public const uint RECV_ID_SIMOBJECT_DATA = 8;
    public const uint RECV_ID_SIMOBJECT_DATA_BYTYPE = 9;
    public const uint RECV_ID_CLIENT_DATA = 10;
    public const uint RECV_ID_SYSTEM_STATE = 15;
    public const uint RECV_ID_EVENT_FILENAME = 6;

    // ---------------- 事件发送标志 ----------------

    public const uint EVENT_FLAG_DEFAULT = 0;
    public const uint EVENT_FLAG_GROUPID_IS_PRIORITY = 0x00000010;

    // ---------------- 数据类型 ----------------

    public const uint DATATYPE_INT32 = 1;
    public const uint DATATYPE_INT64 = 2;
    public const uint DATATYPE_FLOAT32 = 3;
    public const uint DATATYPE_FLOAT64 = 4;
    public const uint DATATYPE_STRING8 = 5;
    public const uint DATATYPE_STRING32 = 6;
    public const uint DATATYPE_STRING64 = 7;
    public const uint DATATYPE_STRING128 = 8;
    public const uint DATATYPE_STRING256 = 9;
    public const uint DATATYPE_STRING260 = 10;

    // ---------------- 数据请求周期 ----------------

    public const uint PERIOD_NEVER = 0;
    public const uint PERIOD_ONCE = 1;
    public const uint PERIOD_VISUAL_FRAME = 2;
    public const uint PERIOD_SIM_FRAME = 3;
    public const uint PERIOD_SECOND = 4;

    /// <summary>用户自己的飞机。</summary>
    public const uint OBJECT_ID_USER = 0;

    // ---------------- 客户端数据区（共享内存） ----------------
    //
    // MobiFlight WASM 模块就是靠这套 API 和外部程序交换数据的：
    // 外部程序把命令写进一个共享内存区，模块把变量值写回另一个。

    public const uint CLIENT_DATA_PERIOD_NEVER = 0;
    public const uint CLIENT_DATA_PERIOD_ONCE = 1;
    public const uint CLIENT_DATA_PERIOD_VISUAL_FRAME = 2;
    public const uint CLIENT_DATA_PERIOD_ON_SET = 3;
    public const uint CLIENT_DATA_PERIOD_SECOND = 4;

    public const uint CLIENT_DATA_SET_FLAG_DEFAULT = 0;
    public const uint CLIENT_DATA_SET_FLAG_TAGGED = 1;

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_MapClientDataNameToID(
        IntPtr hSimConnect,
        string szClientDataName,
        uint clientDataId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_AddToClientDataDefinition(
        IntPtr hSimConnect,
        uint defineId,
        uint offset,
        uint sizeOrType,
        float epsilon,
        uint datumId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_RequestClientData(
        IntPtr hSimConnect,
        uint clientDataId,
        uint requestId,
        uint defineId,
        uint period,
        uint flags,
        uint origin,
        uint interval,
        uint limit);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_SetClientData(
        IntPtr hSimConnect,
        uint clientDataId,
        uint defineId,
        uint flags,
        uint reserved,
        uint unitSize,
        IntPtr dataSet);

    // ---------------- 接收缓冲区的字段偏移 ----------------
    //
    // 直接用偏移量而不是 marshal 整个结构体：SimConnect 的接收结构体是
    // "公共头 + 各自不同的尾巴"，版本之间尾巴还会变。只读固定不变的头几个字段更稳。

    /// <summary>SIMCONNECT_RECV 公共头：dwSize / dwVersion / dwID。</summary>
    public const int RecvOffsetId = 8;
    public const int RecvHeaderSize = 12;

    /// <summary>SIMCONNECT_RECV_EXCEPTION 的 dwException，紧跟在公共头之后。</summary>
    public const int RecvOffsetException = 12;

    /// <summary>SIMCONNECT_RECV_SIMOBJECT_DATA 里 dwDefineCount（本次返回了几个数据项）。</summary>
    public const int RecvOffsetDefineCount = 36;

    /// <summary>SIMCONNECT_RECV_SIMOBJECT_DATA 里 dwData（实际数据起点）。</summary>
    public const int RecvOffsetSimObjectData = 40;

    /// <summary>SIMCONNECT_RECV_EVENT 里 uEventID。</summary>
    public const int RecvOffsetEventId = 16;

    // ---------------- 函数声明 ----------------

    /// <summary>
    /// 回调签名。必须保持托管引用存活（见 SimConnectOutput 里的字段），否则会被 GC 回收导致崩溃。
    /// </summary>
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void DispatchProc(IntPtr pData, uint cbData, IntPtr pContext);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_Open(
        out IntPtr phSimConnect,
        string szName,
        IntPtr hWnd,
        uint userEventWin32,
        IntPtr hEventHandle,
        uint configIndex);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_Close(IntPtr hSimConnect);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_RequestSystemState(IntPtr handle, uint requestId, string state);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_SubscribeToSystemEvent(IntPtr handle, uint eventId, string name);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_CallDispatch(
        IntPtr hSimConnect,
        DispatchProc pfcnDispatch,
        IntPtr pContext);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_MapClientEventToSimEvent(
        IntPtr hSimConnect,
        uint eventId,
        string eventName);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_TransmitClientEvent(
        IntPtr hSimConnect,
        uint objectId,
        uint eventId,
        uint dwData,
        uint groupId,
        uint flags);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_AddToDataDefinition(
        IntPtr hSimConnect,
        uint defineId,
        string datumName,
        string? unitsName,
        uint datumType,
        float epsilon,
        uint datumId);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_RequestDataOnSimObject(
        IntPtr hSimConnect,
        uint requestId,
        uint defineId,
        uint objectId,
        uint period,
        uint flags,
        uint origin,
        uint interval,
        uint limit);

    [DllImport(DllName, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_SetDataOnSimObject(
        IntPtr hSimConnect,
        uint defineId,
        uint objectId,
        uint flags,
        uint arrayCount,
        uint cbUnitSize,
        IntPtr pDataSet);

    [DllImport(DllName, CharSet = CharSet.Ansi, CallingConvention = CallingConvention.StdCall)]
    public static extern int SimConnect_GetLastSentPacketID(IntPtr hSimConnect, out uint pdwError);

    /// <summary>HRESULT 是否表示成功。</summary>
    public static bool Succeeded(int hr) => hr >= 0;
}
