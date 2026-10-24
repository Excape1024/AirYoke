namespace AirYoke.Bridge.Sim;

/** 验证实际输出类的事件序列，不加载原生库，也不驱动飞机。 */
internal static class ControlSelfTest
{
    public static bool Run()
    {
        long now = 1000;
        var events = new List<(uint Id, uint Data)>();
        using var output = new SimConnectOutput((id, data) => events.Add((id, data)), () => now);
        var state = new Protocol.State(1, 4, 0, 0, 0, -32767, 0x6F00, 5119, 0);
        output.Apply(state); output.TestTick();
        bool Has(uint id, uint data) => events.Contains((id, data));
        void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        try
        {
            Require(Has(7, 1), "未校准也应能使用独立的反推操作");
            Require(Has(20, 16383) && Has(21, 16383), "双轮刹车应完全按下");
            Require(Has(22, 1) && Has(24, 0) && Has(23, 16383) && Has(12, 0), "辅助字段映射错误");
            events.Clear();
            output.Apply(state with { Throttle = 0, Buttons = 0, Aux = 0 }); output.TestTick();
            Require(Has(7, 0) && Has(4, unchecked((uint)-16383)), "反推释放应关闭保持并回慢车");
            Require(Has(20, unchecked((uint)-16383)) && Has(21, unchecked((uint)-16383)), "刹车释放不能发轴中点");
            events.Clear();
            output.Apply(state); output.TestTick(); events.Clear(); now += 501; output.TestTick();
            Require(Has(7, 0) && Has(20, unchecked((uint)-16383)), "断线看门狗应释放反推和刹车");
            Require(!events.Any(e => e.Id == 5 || e.Id == 23), "断线不能重复反推或减速板指令");
            events.Clear(); output.TestTick();
            Require(events.Count == 0, "失联释放后应停止写入操纵面");
            output.Apply(state with { Throttle = 29162, Buttons = 0, Aux = 0 }); output.TestTick();
            Require(events.Any(e => e.Id == 4 && e.Data > 12770 && e.Data < 12790), "CL 目标必须保持在约 89% 而非零");
            events.Clear(); output.Apply(state with { Flags = 0, Throttle = 0, Buttons = 0 }); output.TestTick();
            Require(!events.Any(e => e.Id == 4), "交还后不能持续写零油门");
            // 自动驾驶断开采用独立位和关闭事件，不得复用开关切换。
            var disconnect = state with { Flags = 0, Throttle = 0, Buttons = Protocol.ButtonApDisconnect, Aux = 0 };
            var packet = new byte[Protocol.StateSize];
            Protocol.EncodeState(disconnect, packet);
            Require(Protocol.TryDecodeState(packet, out var decoded) && decoded.Equals(disconnect), "断开指令协议往返失败");
            events.Clear(); output.Apply(disconnect); output.TestTick();
            Require(Has(28, 0) && !events.Any(e => e.Id == 13 || e.Id == 4), "断开必须只发送关闭事件，不得切换或写油门");
            events.Clear(); now += 20; output.Apply(disconnect); output.TestTick();
            Require(!events.Any(e => e.Id == 28), "断开重发应限制在10Hz以内");
            now += 80; output.Apply(disconnect); output.TestTick();
            Require(events.Count(e => e.Id == 28) == 1 && !events.Any(e => e.Id == 13), "断开重发不得重新接通");
            events.Clear(); output.Apply(disconnect with { Buttons = 0 }); now += 100; output.TestTick();
            Require(!events.Any(e => e.Id == 28), "发送窗口结束后必须停止断开指令");
            events.Clear(); output.Apply(disconnect with { Buttons = Protocol.ButtonApDisconnect | ButtonMask.Autopilot }); output.TestTick();
            Require(Has(28, 0) && !events.Any(e => e.Id == 13), "同时收到开关与断开时必须以断开为准");
            events.Clear(); now += 501; output.TestTick();
            Require(!events.Any(e => e.Id == 28), "断线看门狗不能重发断开指令");
            events.Clear(); output.Apply(disconnect with { Buttons = 0 }); output.TestTick();
            Require(!events.Any(e => e.Id == 28), "重连后无新请求不能重放断开指令");
            var telemetry = output.BuildTelemetry(42);
            Require((telemetry.SystemFlags & Protocol.SystemApDisconnectSupported) != 0, "必须公布断开能力位");
            var telemetryPacket = new byte[Protocol.TelemetrySize];
            Protocol.EncodeTelemetry(telemetry, telemetryPacket);
            Require(Protocol.TryDecodeTelemetry(telemetryPacket, out var telemetryBack) && telemetryBack.Equals(telemetry), "能力位遥测往返失败");
            return true;
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return false; }
    }
}
