using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>单独订阅机模变量。与基础遥测分开有效，缺失时绝不退回通用推力变量。</summary>
internal sealed class AircraftTelemetry(Action<string> log, Func<long> clock)
{
    internal const uint IdentityRequest = 900, LoadedEvent = 901;
    internal const int FieldCount = 31;
    private readonly object _gate = new();
    private readonly double[][] _values = [new double[FieldCount], new double[FieldCount]];
    private readonly long[][] _times = [new long[FieldCount], new long[FieldCount]];
    private string _path = "";
    private byte _actual = AircraftProfile.Unknown;
    private long _identityAt, _lastRequest;

    internal static string[] Fields(byte profile)
    {
        string prefix = AircraftProfile.Prefix(profile);
        var names = new List<string>
        {
            $"L:{prefix}_AUTOTHRUST_TLA:1", $"L:{prefix}_AUTOTHRUST_TLA:2",
            profile == AircraftProfile.IniBuilds ? "L:INI_ENG1_N1" : "L:A32NX_ENGINE_N1:1",
            profile == AircraftProfile.IniBuilds ? "L:INI_ENG2_N1" : "L:A32NX_ENGINE_N1:2",
            $"L:{prefix}_THROTTLE_MAPPING_USE_REVERSE_ON_AXIS:1",
            $"L:{prefix}_THROTTLE_MAPPING_USE_REVERSE_ON_AXIS:2"
        };
        for (int engine = 1; engine <= 2; engine++)
            foreach (string detent in new[] { "REVERSE", "REVERSE_IDLE", "IDLE", "CLIMB", "FLEXMCT", "TOGA" })
                foreach (string edge in new[] { "LOW", "HIGH" })
                    names.Add($"L:{prefix}_THROTTLE_MAPPING_{detent}_{edge}:{engine}");
        names.Add($"L:{prefix}_AUTOTHRUST_STATUS");
        return names.ToArray();
    }

    internal void Subscribe(IntPtr handle)
    {
        Reset();
        for (byte profile = 1; profile <= 2; profile++)
        {
            var names = Fields(profile);
            for (int i = 0; i < names.Length; i++)
            {
                uint id = (uint)(profile * 100 + i);
                Check(SimConnectApi.SimConnect_AddToDataDefinition(handle, id, names[i], "Number",
                    SimConnectApi.DATATYPE_FLOAT64, 0, 0), names[i]);
                Check(SimConnectApi.SimConnect_RequestDataOnSimObject(handle, id, id, 0,
                    SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0), names[i]);
            }
        }
        Check(SimConnectApi.SimConnect_SubscribeToSystemEvent(handle, LoadedEvent, "AircraftLoaded"), "机模切换通知");
        Poll(handle);
    }

    private void Check(int hr, string field)
    {
        if (!SimConnectApi.Succeeded(hr)) log($"机模订阅失败：{field}，错误 0x{hr:X8}");
    }

    internal void Poll(IntPtr handle)
    {
        if (_lastRequest != 0 && clock() - _lastRequest < 500) return;
        _lastRequest = clock();
        Check(SimConnectApi.SimConnect_RequestSystemState(handle, IdentityRequest, "AircraftLoaded"), "飞机身份");
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _actual = AircraftProfile.Unknown; _path = ""; _identityAt = 0; _lastRequest = 0;
            foreach (var times in _times) Array.Clear(times);
        }
    }

    internal void SetIdentity(string path, bool forceReset = false)
    {
        lock (_gate)
        {
            if (forceReset || !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            {
                foreach (var times in _times) Array.Clear(times);
                _path = path;
                _actual = AircraftProfile.Identify(path);
                log($"飞机身份：{_actual}，{path}");
            }
            _identityAt = clock();
        }
    }

    internal bool Dispatch(IntPtr data, uint size, uint message)
    {
        if (message == SimConnectApi.RECV_ID_SYSTEM_STATE && size >= 284 &&
            (uint)Marshal.ReadInt32(data, 12) == IdentityRequest)
        {
            SetIdentity((Marshal.PtrToStringAnsi(data + 24, 260) ?? "").Split('\0')[0]);
            return true;
        }
        if (message == SimConnectApi.RECV_ID_EVENT_FILENAME && size >= 284 &&
            (uint)Marshal.ReadInt32(data, 16) == LoadedEvent)
        {
            SetIdentity((Marshal.PtrToStringAnsi(data + 24, 260) ?? "").Split('\0')[0], true);
            return true;
        }
        if (message != SimConnectApi.RECV_ID_SIMOBJECT_DATA || size < 48) return false;
        int request = Marshal.ReadInt32(data, 12);
        int profile = request / 100, index = request % 100;
        if (profile is < 1 or > 2 || index >= FieldCount) return false;
        Record((byte)profile, index, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(data, 40)));
        return true;
    }

    internal void Record(byte profile, int index, double value)
    {
        lock (_gate)
        {
            _values[profile - 1][index] = value;
            // 非有限值立即使该字段失效，不能继续将旧样本标成新鲜。
            _times[profile - 1][index] = double.IsFinite(value) ? clock() : 0;
        }
    }

    internal Snapshot Read(byte selected)
    {
        lock (_gate)
        {
            long now = clock();
            bool matched = selected == _actual && _identityAt != 0 && now - _identityAt < 1500;
            if (selected is < 1 or > 2 || !matched) return new Snapshot(selected, 16, 0, 0, 0, 0, 65535, false, [], []);
            var values = _values[selected - 1]; var times = _times[selected - 1];
            bool Fresh(int index, int timeout = 500) => times[index] != 0 && now - times[index] < timeout;
            bool levers = Fresh(0) && Fresh(1) && AircraftProfile.ValidAngle(values[0]) && AircraftProfile.ValidAngle(values[1]);
            bool n1 = Fresh(2) && Fresh(3) && values[2] is >= 0 and <= 120 && values[3] is >= 0 and <= 120;
            bool calibration = true;
            for (int i = 4; i < 30; i++) calibration &= Fresh(i, 1500);
            var left = values[6..18]; var right = values[18..30];
            bool reverseLeft = values[4] > .5, reverseRight = values[5] > .5;
            calibration &= AircraftProfile.TryMapAxis(0, left, reverseLeft, out _) &&
                AircraftProfile.TryMapAxis(0, right, reverseRight, out _);
            // 有效校准也是局部变量已初始化的证据，防止机模加载阶段的默认零被显示成慢车。
            levers &= calibration;
            n1 &= calibration;
            int flags = (levers ? 1 : 0) | (n1 ? 2 : 0) | (levers && calibration ? 4 : 0) |
                (calibration && reverseLeft && reverseRight ? 8 : 0);
            // 无效的 N1 不能把仍有效的杆位年龄变成 65535，反之亦然。
            long oldest = long.MaxValue;
            if (levers) oldest = Math.Min(times[0], times[1]);
            if (n1) oldest = Math.Min(oldest, Math.Min(times[2], times[3]));
            long age = oldest == long.MaxValue ? 65535 : now - oldest;
            return new Snapshot(selected, (byte)flags,
                levers ? AircraftProfile.LeverFromAngle(values[0]) : 0,
                levers ? AircraftProfile.LeverFromAngle(values[1]) : 0,
                n1 ? (float)values[2] : 0, n1 ? (float)values[3] : 0,
                (ushort)Math.Clamp(age, 0, 65535), Fresh(30) && values[30] == 2, left, right);
        }
    }

    internal sealed record Snapshot(byte Aircraft, byte Flags, float Left, float Right, float N1Left,
        float N1Right, ushort Age, bool Autothrust, double[] LeftBounds, double[] RightBounds)
    {
        internal bool Ready => (Flags & 4) != 0;
        internal bool TryAxes(float lever, out int left, out int right)
        {
            left = right = 0;
            bool reverse = (Flags & 8) != 0;
            return Ready && AircraftProfile.TryMapAxis(lever, LeftBounds, reverse, out left) &&
                AircraftProfile.TryMapAxis(lever, RightBounds, reverse, out right);
        }
    }
}
