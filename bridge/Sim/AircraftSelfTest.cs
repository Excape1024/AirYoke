namespace AirYoke.Bridge.Sim;

/// <summary>使用实际桥接输出和内存遥测，验证机模隔离、控制事件及协议字节。</summary>
internal static class AircraftSelfTest
{
    internal static bool Run()
    {
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }
        try
        {
            Check(AircraftProfile.Identify(@"SimObjects\Airplanes\microsoft-aircraft-a320neo\aircraft.cfg") == 1, "V2 身份识别失败");
            Check(AircraftProfile.Identify(@"SimObjects\Airplanes\FlyByWire_A320_NEO\aircraft.cfg") == 2, "FBW 身份识别失败");
            Check(AircraftProfile.Identify("FlyByWire A380") == 255, "不能把 A380 当成 A320");
            double[] calibration = [-1, -.95, -.8, -.7, -.55, -.45, .05, .15, .45, .55, .95, 1];
            Check(AircraftProfile.TryMapAxis(25f / 45, calibration, true, out int cl) && cl == 1638, "CL 应映射实际校准中心");
            Check(AircraftProfile.TryMapAxis(35f / 45, calibration, true, out int flex) && flex == 8192, "FLX 校准映射错误");
            Check(!AircraftProfile.TryMapAxis(0, new double[12], true, out _), "未初始化的零校准不能使用");
            Check(!AircraftProfile.TryMapAxis(-1, calibration, false, out _), "关闭轴反推时不得发送负轴目标");
            var invalid = (double[])calibration.Clone(); invalid[7] = double.NaN;
            Check(!AircraftProfile.TryMapAxis(0, invalid, true, out _), "非有限校准必须拒绝");
            invalid = (double[])calibration.Clone(); invalid[6] = -.9;
            Check(!AircraftProfile.TryMapAxis(0, invalid, true, out _), "交叉卡位必须拒绝");
            foreach (byte model in new byte[] { 1, 2 })
            {
                long now = 1000;
                var events = new List<(uint Id, uint Data)>();
                using var output = new SimConnectOutput((id, data) => events.Add((id, data)), () => now);
                var source = output.TestAircraft;
                string path = model == 1 ? "microsoft-aircraft-a320neo" : "FlyByWire_A320_NEO";
                source.SetIdentity(path);
                void Feed(float angle = 25, float n1 = 60)
                {
                    source.SetIdentity(path);
                    for (int i = 0; i < AircraftTelemetry.FieldCount; i++) source.Record(model, i, 0);
                    source.Record(model, 0, angle); source.Record(model, 1, angle);
                    source.Record(model, 2, n1); source.Record(model, 3, n1 + 1);
                    source.Record(model, 4, 1); source.Record(model, 5, 1);
                    for (int i = 0; i < 12; i++)
                    {
                        source.Record(model, 6 + i, calibration[i]);
                        source.Record(model, 18 + i, calibration[i]);
                    }
                    source.Record(model, 30, 2);
                }
                Feed();
                var state = new Protocol.State(1, (byte)(model << 3), 0, 0, 0, (short)(32767 * 25 / 45), 0, 0, 0);
                output.Apply(state); output.TestTick(); events.Clear();
                state = state with { Flags = (byte)(state.Flags | 4) };
                output.Apply(state); output.TestTick();
                Check(events.Contains((30u, 1638u)) && events.Contains((31u, 1638u)), "必须给双发动机发送校准后的轴值");
                Check(!events.Any(e => e.Id is 4 or 5 or 6 or 7), "专用机模不能混发原版油门和反推事件");
                source.Record(model, 24, .2); source.Record(model, 25, .3);
                events.Clear(); output.Apply(state); output.TestTick();
                Check(events.Contains((30u, 1638u)) && events.Contains((31u, 4096u)), "双轴校准不同必须分别映射");
                Feed();
                var first = output.BuildTelemetry(42);
                now += 100; Feed(25, 83);
                var next = output.BuildTelemetry(43);
                Check(first.ThrottlePos == next.ThrottlePos && next.ThrottlePos2 == next.ThrottlePos && next.N1Left == 83,
                    "自动推力改变 N1 时不得移动手机杆位");
                source.Record(model, 1, 35);
                Check(output.BuildTelemetry(44).ThrottlePos2 != next.ThrottlePos, "两杆不一致时不得合并或平均");
                var bytes = new byte[Protocol.TelemetrySize]; Protocol.EncodeTelemetry(next, bytes);
                Check(bytes.Length == 63 && bytes[2] == 5 && bytes[45] == model && bytes[46] == 15, "协议五扩展布局错误");
                Check(Protocol.TryDecodeTelemetry(bytes, out var decoded) && decoded == next, "扩展遥测往返失败");
                bytes[2] = 4;
                Check(!Protocol.TryDecodeTelemetry(bytes, out _), "必须拒绝旧协议遥测");
                Feed(-20);
                events.Clear(); output.Apply(state with { Throttle = -32767 }); output.TestTick();
                Check(events.Any(e => e.Id == 30 && unchecked((int)e.Data) < 0), "反推应走每根校准轴");
                events.Clear(); now += 501; output.TestTick();
                Check(events.Contains((30u, unchecked((uint)-8192))) && events.Contains((31u, unchecked((uint)-8192))), "断线应释放双轴反推到缓存慢车");
                Check((source.Read(model).Flags & 3) == 0, "过期杆位和 N1 必须失效");
                Feed(); events.Clear(); output.Apply(state); output.TestTick();
                Check(!events.Any(e => e.Id is 30 or 31), "反馈恢复不能自行重放旧接管");
                output.Apply(state with { Flags = (byte)(model << 3) }); output.TestTick();
                events.Clear(); output.Apply(state); output.TestTick();
                Check(events.Any(e => e.Id == 30), "交还后重新接管应恢复控制");
                source.SetIdentity("Cessna 172");
                events.Clear(); output.Apply(state); output.TestTick();
                Check(!events.Any(e => e.Id is 30 or 31) && (source.Read(model).Flags & 16) != 0, "错误机模必须停止油门并提示不匹配");
                source.SetIdentity(path);
                Check((source.Read(model).Flags & 3) == 0, "切回机模不能复用上一次的旧反馈");
                Feed(); source.Record(model, 0, double.NaN);
                Check((source.Read(model).Flags & 1) == 0, "非法杆位必须立即失效");
                Feed(); source.Record(model, 2, double.NaN);
                var partial = source.Read(model);
                Check((partial.Flags & 1) != 0 && (partial.Flags & 2) == 0 && partial.Age < 500,
                    "N1 失效不能污染有效杆位年龄");
            }
            Console.WriteLine("机模、双杆、双发 N1、失效隔离与协议五自检通过");
            return true;
        }
        catch (InvalidOperationException ex) { Console.Error.WriteLine(ex.Message); return false; }
    }
}
