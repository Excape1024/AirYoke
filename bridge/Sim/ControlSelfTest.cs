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
            return true;
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return false; }
    }
}
