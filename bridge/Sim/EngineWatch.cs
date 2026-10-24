using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>持续十秒的只读杆位诊断，不接收手机、不映射或发送任何控制事件。</summary>
internal static class EngineWatch
{
    internal static int Run(byte profile)
    {
        if (!SimConnectApi.EnsureLoaded()) { Console.Error.WriteLine("未找到模拟器自带 SimConnect.dll。"); return 1; }
        int hr = SimConnectApi.SimConnect_Open(out var handle, "AirYoke 杆位只读诊断", IntPtr.Zero, 0, IntPtr.Zero, 0);
        if (!SimConnectApi.Succeeded(hr)) { Console.Error.WriteLine($"模拟器未连接：0x{hr:X8}"); return 1; }
        var source = new AircraftTelemetry(Console.WriteLine, () => Environment.TickCount64);
        bool quit = false, observed = false;
        SimConnectApi.DispatchProc callback = (data, size, _) =>
        {
            if (data == IntPtr.Zero || size < 12) return;
            uint message = (uint)Marshal.ReadInt32(data, 8);
            if (message == SimConnectApi.RECV_ID_QUIT) quit = true;
            if (message == SimConnectApi.RECV_ID_EXCEPTION && size >= 20)
                Console.WriteLine($"订阅异常：{Marshal.ReadInt32(data, 12)}，发送序号 {Marshal.ReadInt32(data, 16)}");
            source.Dispatch(data, size, message);
        };
        try
        {
            source.Subscribe(handle);
            Console.WriteLine("只读观察十秒；有效位：1=双杆，2=双发N1，4=正推控制可用，8=轴反推可用，16=机模未匹配。");
            long start = Environment.TickCount64, last = 0;
            while (!quit && Environment.TickCount64 - start < 10000)
            {
                SimConnectApi.SimConnect_CallDispatch(handle, callback, IntPtr.Zero);
                source.Poll(handle);
                if (Environment.TickCount64 - last >= 500)
                {
                    last = Environment.TickCount64;
                    var t = source.Read(profile);
                    observed |= (t.Flags & 3) == 3;
                    string Lever(float v) => (v * (v < 0 ? 20 : 45)).ToString("F2");
                    Console.WriteLine($"机模={profile} 有效位={t.Flags} 杆角=({Lever(t.Left)}, {Lever(t.Right)}) N1=({t.N1Left:F1}, {t.N1Right:F1}) 样本年龄={t.Age}ms");
                }
                Thread.Sleep(10);
            }
        }
        finally { SimConnectApi.SimConnect_Close(handle); GC.KeepAlive(callback); }
        if (!observed) Console.Error.WriteLine("未取得完整有效机模反馈；请保留日志并核对实际机模与 EFB 校准。");
        return observed ? 0 : 1;
    }
}
