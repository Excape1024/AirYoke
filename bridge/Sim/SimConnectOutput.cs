using System.Runtime.InteropServices;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 通过 SimConnect 把手机发来的操纵量送进模拟器。
///
/// ## 事件名与方向都是实测的，不是查文档
///
/// 见 <see cref="AxisSignProbe"/>。实测结论（在 F/A-18E 与 A320neo 上均验证）：
///
/// | 轴 | 事件 | 方向 |
/// |---|---|---|
/// | 副翼 | `AXIS_AILERONS_SET` | 反号，需取负 |
/// | 升降舵 | `AXIS_ELEVATOR_SET` | 反号，需取负 |
/// | 方向舵 | `AXIS_RUDDER_SET` | 反号，需取负 |
/// | 油门 | `AXIS_THROTTLE_SET` | 同号 |
///
/// 注意 `AXIS_AILERON_SET`（单数）是**无效的**，必须用 `AXIS_AILERONS_SET`（复数）。
///
/// ## 线程模型
///
/// **SimConnect 不是线程安全的**，所有调用必须来自同一个线程。
/// 所以 [Apply] 只把最新一帧存下来（加锁），真正的发送与消息泵都在
/// [PumpLoop] 这一个线程里做。
/// </summary>
public sealed class SimConnectOutput : ISimOutput
{
    // ---- 事件 ID ----
    private const uint EvAileron = 1;
    private const uint EvElevator = 2;
    private const uint EvRudder = 3;
    private const uint EvThrottle = 4;

    // 反推专用。实测结论：反推**不在油门轴上** ——
    // 轴的负向最末端虽然有一小段反推区，但最多只能把反推装置推到 4%，
    // 而按住 F2 能到 18.58%（用户实测，ECAM 出现 REV、发动机声音明显变大）。
    // 所以反推必须走"增减推力"事件，也就是 F2 走的那条路。
    private const uint EvThrottleDecr = 5;
    private const uint EvThrottleIncr = 6;
    private const uint EvReverseHold = 7;

    private const uint EvGearToggle = 10;
    private const uint EvFlapsUp = 11;
    private const uint EvFlapsDown = 12;
    private const uint EvApMaster = 13;
    private const uint EvParkingBrakes = 14;
    private const uint EvPause = 15;
    private const uint EvBrakeLeft = 20, EvBrakeRight = 21, EvParkingSet = 22;
    private const uint EvSpoilersSet = 23, EvSpoilersArm = 24;
    private const uint EvFlaps1 = 25, EvFlaps2 = 26, EvFlaps3 = 27;
    private const uint EvApDisconnect = 28;
    private const uint EvThrottle1 = 30, EvThrottle2 = 31;
    private readonly AircraftTelemetry _aircraftTelemetry;
    private byte _activeAircraft;
    private bool _requireThrottleRelease;
    private bool _customReverse;
    private int _idleLeft, _idleRight;

    // ---- 遥测订阅 ----
    private const uint DefineTelemetry = 1;
    private const uint RequestTelemetry = 1;

    /// <summary>遥测字段数。顺序见 <see cref="DefineTelemetryFields"/>。</summary>
    private const int TelemetryFieldCount = 14;

    private const int FieldSpoilers = 9;
    private const int FieldThrottleLever = 10;
    private const int FieldAutothrottle = 11;

    /// <summary>轴量满量程。SimConnect 的轴事件用 ±16383。</summary>
    private const float AxisFullScale = 16383f;

    /// <summary>油门轴在"慢车"处的取值（本 App 的 0 映射到这里）。</summary>
    private const int IdleAxisData = -16383;

    /// <summary>发送间隔。50Hz 对操纵面足够，也比 100Hz 少一半调用。</summary>
    private const int SendIntervalMs = 20;

    /// <summary>
    /// 反推区里每 20ms 发几个 <c>THROTTLE_DECR</c>。滑条推到底时约合 100 步/秒。
    ///
    /// 这个数只能靠手感定：太小则反推迟迟进不去，太大则一次跳到底、没有过渡。
    /// </summary>
    private const double ReverseStepsPerTick = 2.0;

    /// <summary>退出反推时发几次 <c>THROTTLE_INCR</c>，确保真的从反推区里出来。</summary>
    private const int ExitReverseSteps = 8;

    private readonly Action<string> _log;
    private readonly Action<uint, uint>? _eventSink;
    private readonly Func<long> _clock = () => Environment.TickCount64;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _stateLock = new();

    private IntPtr _handle = IntPtr.Zero;
    private SimConnectApi.DispatchProc? _dispatch;
    private Task? _pumpTask;

    private Protocol.State _latest;
    private bool _hasState;
    private long _lastInputMs;
    private long _lastTelemetryMs;
    private bool _braking;
    private long _lastAuxMs;
    private ushort _lastButtons;
    private bool _apDisconnectActive;
    private long _lastApDisconnectMs;

    /// <summary>当前是否处于反推状态。用于在进/出反推的那一刻做一次性的切换动作。</summary>
    private bool _inReverse;

    /// <summary>
    /// 反推步进的攒数器。
    ///
    /// 滑条深度是连续的，而 <c>THROTTLE_DECR</c> 是离散的"减一步"。
    /// 不攒的话，浅反推（比如 0.3）每拍算出来不到 1 步，会被取整成 0 —— 永远推不进去。
    /// </summary>
    private double _reverseAccum;

    private double _lastSendMs;

    /// <summary>遥测字段，顺序见 <see cref="DefineTelemetryFields"/>。</summary>
    private readonly double[] _telemetry = new double[TelemetryFieldCount];
    private readonly long[] _telemetryTimes = new long[TelemetryFieldCount];

    public SimConnectOutput(Action<string> log)
    {
        _log = log;
        _aircraftTelemetry = new AircraftTelemetry(log, _clock);
    }

    // 自检使用内存事件接收器，完全不加载或连接模拟器。
    internal SimConnectOutput(Action<uint, uint> eventSink, Func<long> clock)
    {
        _log = _ => { };
        _eventSink = eventSink;
        _clock = clock;
        _aircraftTelemetry = new AircraftTelemetry(_log, clock);
    }
    internal void TestTick() => SendLatest();
    internal AircraftTelemetry TestAircraft => _aircraftTelemetry;

    public string Name => "SimConnect";

    public bool IsConnected => _handle != IntPtr.Zero;

    // ---------------- 连接 ----------------

    public bool Connect()
    {
        if (_handle != IntPtr.Zero) return true;

        if (!SimConnectApi.EnsureLoaded())
        {
            _log("未找到 SimConnect.dll —— 手机数据将不会驱动飞机。" +
                 "它随模拟器一起安装；若模拟器装在非常规位置，可把该 DLL 拷到本程序目录。");
            return false;
        }

        _dispatch = OnDispatch;

        int hr = SimConnectApi.SimConnect_Open(
            out IntPtr handle, "AirYoke Bridge", IntPtr.Zero, 0, IntPtr.Zero, 0);

        if (!SimConnectApi.Succeeded(hr))
        {
            _log($"连接模拟器失败，HRESULT=0x{hr:X8}（模拟器没运行，或还在加载主界面）");
            return false;
        }

        _handle = handle;
        lock (_telemetry) { Array.Clear(_telemetry); Array.Clear(_telemetryTimes); }

        MapEvents();
        SubscribeTelemetry();
        _aircraftTelemetry.Subscribe(_handle);

        _pumpTask = Task.Run(() => PumpLoop(_cts.Token));
        _log($"已连接模拟器，SimConnect.dll = {SimConnectApi.ResolvedPath}");
        return true;
    }

    private void MapEvents()
    {
        // 事件名是实测确认过的：AXIS_AILERONS_SET 是复数，单数那个无效
        Map(EvAileron, "AXIS_AILERONS_SET");
        Map(EvElevator, "AXIS_ELEVATOR_SET");
        Map(EvRudder, "AXIS_RUDDER_SET");
        Map(EvThrottle, "AXIS_THROTTLE_SET");
        Map(EvThrottle1, "THROTTLE1_AXIS_SET_EX1");
        Map(EvThrottle2, "THROTTLE2_AXIS_SET_EX1");
        Map(EvThrottleDecr, "THROTTLE_DECR");
        Map(EvThrottleIncr, "THROTTLE_INCR");
        Map(EvReverseHold, "THROTTLE_REVERSE_THRUST_HOLD");

        Map(EvGearToggle, "GEAR_TOGGLE");
        Map(EvFlapsUp, "FLAPS_UP");
        Map(EvFlapsDown, "FLAPS_DOWN");
        Map(EvApMaster, "AP_MASTER");
        Map(EvApDisconnect, "AUTOPILOT_OFF");
        Map(EvParkingBrakes, "PARKING_BRAKES");
        Map(EvPause, "PAUSE_TOGGLE");
        Map(EvBrakeLeft, "AXIS_LEFT_BRAKE_SET");
        Map(EvBrakeRight, "AXIS_RIGHT_BRAKE_SET");
        Map(EvParkingSet, "PARKING_BRAKE_SET");
        Map(EvSpoilersSet, "SPOILERS_SET");
        Map(EvSpoilersArm, "SPOILERS_ARM_SET");
        Map(EvFlaps1, "FLAPS_1");
        Map(EvFlaps2, "FLAPS_2");
        Map(EvFlaps3, "FLAPS_3");
    }

    private void Map(uint id, string name)
    {
        int hr = SimConnectApi.SimConnect_MapClientEventToSimEvent(_handle, id, name);
        if (!SimConnectApi.Succeeded(hr))
        {
            _log($"事件 {name} 映射失败 0x{hr:X8}（该事件可能不存在，不影响其它功能）");
        }
    }

    /// <summary>
    /// 订阅回传手机的遥测。
    ///
    /// 每一项单独一个 data definition —— 把多个 SimVar 打包进同一个定义时，
    /// 只要有一个名字无效，后面的条目就会整体前移，而读取端的偏移是写死的，
    /// 结果就是读到别的变量的值，且毫无报错。这个坑在标定阶段踩过一次。
    /// </summary>
    private void SubscribeTelemetry()
    {
        var fields = DefineTelemetryFields();
        for (int i = 0; i < fields.Length; i++)
        {
            uint defineId = DefineTelemetry + (uint)i;
            uint requestId = RequestTelemetry + (uint)i;

            SimConnectApi.SimConnect_AddToDataDefinition(
                _handle, defineId, fields[i].Name, fields[i].Units,
                SimConnectApi.DATATYPE_FLOAT64, 0f, 0);

            SimConnectApi.SimConnect_RequestDataOnSimObject(
                _handle, requestId, defineId, SimConnectApi.OBJECT_ID_USER,
                SimConnectApi.PERIOD_SIM_FRAME, 0, 0, 0, 0);
        }
    }

    private static (string Name, string Units)[] DefineTelemetryFields() => new[]
    {
        ("AIRSPEED INDICATED", "Knots"),
        ("PLANE ALTITUDE", "Feet"),
        ("PLANE HEADING DEGREES MAGNETIC", "Degrees"),
        ("PLANE PITCH DEGREES", "Degrees"),
        ("PLANE BANK DEGREES", "Degrees"),
        ("VERTICAL SPEED", "Feet per minute"),
        ("GEAR HANDLE POSITION", "Bool"),
        ("FLAPS HANDLE INDEX", "Number"),
        ("AUTOPILOT MASTER", "Bool"),
        ("SPOILERS HANDLE POSITION", "Percent Over 100"),
        // 仅供原版兼容；第三方空客必须使用独立杆位，不能把该值当成座舱杆角。
        ("GENERAL ENG THROTTLE LEVER POSITION:1", "Percent Over 100"),
        // 仅显示自动油门状态，不从它学习卡位。
        ("AUTOTHROTTLE ACTIVE", "Bool"),
        ("SPOILERS ARMED", "Bool"),
        ("BRAKE PARKING INDICATOR", "Bool"),
    };

    // ---------------- 发送 ----------------

    /// <summary>由桥接主循环调用。只记录，不做 I/O（SimConnect 不是线程安全的）。</summary>
    public void Apply(in Protocol.State state)
    {
        lock (_stateLock)
        {
            _latest = state;
            _hasState = true;
            _lastInputMs = _clock();
        }
    }

    private void PumpLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // 收消息（必须与发送在同一线程）
            SimConnectApi.SimConnect_CallDispatch(_handle, _dispatch!, IntPtr.Zero);
            if (_handle == IntPtr.Zero) return;
            _aircraftTelemetry.Poll(_handle);

            double now = Environment.TickCount64;
            if (now - _lastSendMs >= SendIntervalMs)
            {
                _lastSendMs = now;
                SendLatest();
            }

            Thread.Sleep(4);
        }
    }

    private void SendLatest()
    {
        Protocol.State state;
        lock (_stateLock)
        {
            if (!_hasState) return;
            if (_clock() - _lastInputMs > 500)
            {
                ReleaseMomentary();
                _hasState = false;
                _lastButtons = 0;
                return;
            }
            state = _latest;
        }

        // 未校准时不要动操纵面 —— 否则手机随便一放，飞机就跟着乱动
        if (!state.Calibrated)
        {
            SendAxis(EvAileron, 0);
            SendAxis(EvElevator, 0);
            SendAxis(EvRudder, 0);
        }
        else
        {
            SendAxis(EvAileron, -state.AileronNorm);
            SendAxis(EvElevator, -state.ElevatorNorm);
            SendAxis(EvRudder, -state.RudderNorm);
        }

        if (_activeAircraft != state.Aircraft)
        {
            ReleaseCustomReverse();
            if (_inReverse) SendThrottle(0);
            _activeAircraft = state.Aircraft;
            _requireThrottleRelease = true;
        }
        // 机模切换、反馈中断后必须看到交还状态，禁止恢复旧的接管指令。
        if (!state.ThrottleEngaged) _requireThrottleRelease = false;
        if (state.Aircraft != AircraftProfile.Asobo)
        {
            var engine = _aircraftTelemetry.Read(state.Aircraft);
            if (state.ThrottleEngaged && !_requireThrottleRelease && engine.TryAxes(state.ThrottleNorm, out int left, out int right))
            {
                Transmit(EvThrottle1, unchecked((uint)left));
                Transmit(EvThrottle2, unchecked((uint)right));
                engine.TryAxes(0, out _idleLeft, out _idleRight);
                _customReverse = state.ThrottleNorm < 0;
            }
            else
            {
                ReleaseCustomReverse();
                if (state.ThrottleEngaged) _requireThrottleRelease = true;
            }
        }
        else if (state.ThrottleEngaged && !_requireThrottleRelease)
        {
            SendThrottle(state.ThrottleNorm);
        }
        else if (_inReverse) SendThrottle(0f);

        bool braking = (state.Buttons & 0x100) != 0;
        if (braking || _braking)
        {
            uint amount = unchecked((uint)(braking ? 16383 : -16383));
            Transmit(EvBrakeLeft, amount); Transmit(EvBrakeRight, amount);
        }
        _braking = braking;
        // 专用关闭事件可安全重发；首次立即发送，随后最多 10Hz。
        bool disconnect = (state.Buttons & Protocol.ButtonApDisconnect) != 0;
        if (disconnect && (!_apDisconnectActive || _clock() - _lastApDisconnectMs >= 100))
        {
            Transmit(EvApDisconnect);
            _lastApDisconnectMs = _clock();
        }
        _apDisconnectActive = disconnect;
        // 有效位由手机限时发送。重复设置是幂等的，结束后让机内自动系统接管。
        if (_clock() - _lastAuxMs >= 100)
        {
            _lastAuxMs = _clock();
            if ((state.Buttons & 0x200) != 0) Transmit(EvParkingSet, (state.Buttons & 0x400) != 0 ? 1u : 0u);
            if ((state.Buttons & 0x800) != 0) Transmit(EvSpoilersArm, (state.Buttons & 0x1000) != 0 ? 1u : 0u);
            if ((state.Buttons & 0x2000) != 0) Transmit(EvSpoilersSet, (uint)Math.Round((state.Aux & 1023) * 16383.0 / 1023));
            if ((state.Buttons & 0x4000) != 0)
            {
                uint? flapEvent = ((state.Aux >> 10) & 7) switch
                { 0 => EvFlapsUp, 1 => EvFlaps1, 2 => EvFlaps2, 3 => EvFlaps3, 4 => EvFlapsDown, _ => null };
                if (flapEvent.HasValue) Transmit(flapEvent.Value);
            }
        }

        SendButtons(state.Buttons);
    }

    private void ReleaseMomentary()
    {
        ReleaseCustomReverse();
        _requireThrottleRelease = _activeAircraft != AircraftProfile.Asobo;
        _apDisconnectActive = false;
        if (_inReverse) SendThrottle(0f);
        if (_braking)
        {
            Transmit(EvBrakeLeft, unchecked((uint)-16383));
            Transmit(EvBrakeRight, unchecked((uint)-16383));
            _braking = false;
        }
        SendAxis(EvAileron, 0); SendAxis(EvElevator, 0); SendAxis(EvRudder, 0);
    }

    private void ReleaseCustomReverse()
    {
        if (!_customReverse) return;
        // 只对仍然加载的同一机模发送缓存的慢车轴值；切机后不能使用旧标定。
        if ((_aircraftTelemetry.Read(_activeAircraft).Flags & 16) == 0)
        {
            Transmit(EvThrottle1, unchecked((uint)_idleLeft));
            Transmit(EvThrottle2, unchecked((uint)_idleRight));
        }
        _customReverse = false;
    }

    /// <summary>
    /// 把本 App 的油门语义（−1 = 全反推、0 = 慢车、+1 = 满推力）送进模拟器。
    ///
    /// ## 正推力：走轴
    ///
    /// `AXIS_THROTTLE_SET` 的 −16383..+16383 线性对应手柄 0..1，一步到位。
    ///
    /// ## 反推：走增减事件，不走轴
    ///
    /// 反推**不在轴上**。轴的最负端确实有一小段反推区，但 13 点扫描实测它最多只能
    /// 把反推装置推到 **4%**；而按住 F2（等价于持续发 `THROTTLE_DECR`）能到
    /// **18.58%** —— 用户实测那一次 ECAM 出现了 REV 字样、发动机声音明显变大。
    ///
    /// 所以反推照 F2 的做法来：轴压在慢车，另用增减事件把手柄推进反推区。
    /// 滑条越深推得越快，回到正区就把反推收掉。
    /// </summary>
    private void SendThrottle(float normalized)
    {
        if (normalized >= 0f)
        {
            if (_inReverse)
            {
                // 退出反推。多发几次 INCR，确保真的从反推区里出来 ——
                // 只发一次可能只是把反推减小一点，手柄仍留在反推区内。
                for (int i = 0; i < ExitReverseSteps; i++) Transmit(EvThrottleIncr);
                Transmit(EvReverseHold, 0);
                _inReverse = false;
                _reverseAccum = 0;
            }

            uint data = (uint)(int)(Math.Clamp(normalized, 0f, 1f) * 32766f - 16383f);
            Transmit(EvThrottle, data);
            return;
        }

        // ---- 反推区 ----
        if (!_inReverse)
        {
            Transmit(EvReverseHold, 1);
            _inReverse = true;
        }

        // 轴压在慢车：轴量若还停在别处，会把刚推进去的反推顶回来
        Transmit(EvThrottle, unchecked((uint)IdleAxisData));

        _reverseAccum += Math.Abs(normalized) * ReverseStepsPerTick;
        int steps = (int)_reverseAccum;
        _reverseAccum -= steps;
        for (int i = 0; i < steps; i++) Transmit(EvThrottleDecr);
    }

    private void SendAxis(uint eventId, float normalized)
    {
        uint data = (uint)(int)Math.Clamp(normalized * AxisFullScale, -AxisFullScale, AxisFullScale);
        Transmit(eventId, data);
    }

    /// <summary>
    /// 按键按下沿触发一次。
    ///
    /// 只发"新按下的位"，不做重复发送 —— 这些是切换类事件，
    /// 按住不放会疯狂开关。
    /// </summary>
    private void SendButtons(ushort buttons)
    {
        ushort pressed = (ushort)(buttons & ~_lastButtons);
        _lastButtons = buttons;
        if (pressed == 0) return;

        if ((pressed & ButtonMask.Gear) != 0) Transmit(EvGearToggle);
        if ((pressed & ButtonMask.FlapsUp) != 0) Transmit(EvFlapsUp);
        if ((pressed & ButtonMask.FlapsDown) != 0) Transmit(EvFlapsDown);
        if ((pressed & ButtonMask.Autopilot) != 0 && (buttons & Protocol.ButtonApDisconnect) == 0) Transmit(EvApMaster);
        if ((pressed & ButtonMask.ParkingBrake) != 0) Transmit(EvParkingBrakes);
        if ((pressed & ButtonMask.Pause) != 0) Transmit(EvPause);
    }

    private void Transmit(uint eventId) => Transmit(eventId, 0);

    private void Transmit(uint eventId, uint data)
    {
        if (_eventSink != null) { _eventSink(eventId, data); return; }
        SimConnectApi.SimConnect_TransmitClientEvent(
            _handle, SimConnectApi.OBJECT_ID_USER, eventId, data, 0,
            SimConnectApi.EVENT_FLAG_GROUPID_IS_PRIORITY);
    }

    // ---------------- 遥测 ----------------

    public Protocol.Telemetry BuildTelemetry(int seq)
    {
        byte selected;
        lock (_stateLock) selected = _latest.Aircraft;
        var engine = _aircraftTelemetry.Read(selected);
        // 一次性快照，避免逐个字段加锁（每 100ms 一次，锁本身不贵，但读 11 次没必要）
        Span<double> f = stackalloc double[TelemetryFieldCount];
        bool complete = IsConnected;
        lock (_telemetry)
        {
            for (int i = 0; i < TelemetryFieldCount; i++)
            {
                f[i] = _telemetry[i];
                if (i != FieldThrottleLever && i != FieldAutothrottle)
                    complete &= _telemetryTimes[i] != 0 && _clock() - _telemetryTimes[i] < 1500;
            }
        }

        return new Protocol.Telemetry(
            Seq: seq,
            AirspeedKt: (float)f[0],
            AltitudeFt: (float)f[1],
            HeadingDeg: (float)f[2],
            PitchDeg: (float)f[3],
            BankDeg: (float)f[4],
            VerticalSpeedFpm: (float)f[5],
            GearDown: f[6] > 0.5,
            FlapsIndex: (int)Math.Round(f[7]),
            AutopilotOn: f[8] > 0.5,
            LinkQuality: 100,
            ThrottlePos: selected == 0 ? (float)Math.Clamp(f[FieldThrottleLever], 0.0, 1.0) : engine.Left,
            SpoilersPos: (float)Math.Clamp(f[FieldSpoilers], 0.0, 1.0),
            AutothrottleOn: selected == 0 ? f[FieldAutothrottle] > 0.5 : engine.Autothrust,
            SystemFlags: (byte)((complete ? 1 : 0)
                | Protocol.SystemApDisconnectSupported
                | (f[12] > .5 ? 2 : 0) | (f[13] > .5 ? 4 : 0)),
            Aircraft: selected, EngineFlags: IsConnected || _eventSink != null ? engine.Flags : (byte)0,
            ThrottlePos2: engine.Right, N1Left: engine.N1Left, N1Right: engine.N1Right, EngineAgeMs: engine.Age);
    }

    private void OnDispatch(IntPtr pData, uint cbData, IntPtr pContext)
    {
        if (pData == IntPtr.Zero || cbData < 12) return;
        uint messageId = (uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId);
        if (_aircraftTelemetry.Dispatch(pData, cbData, messageId)) return;
        if (messageId == SimConnectApi.RECV_ID_QUIT)
        {
            SimConnectApi.SimConnect_Close(_handle);
            _handle = IntPtr.Zero;
            lock (_stateLock) _hasState = false;
            _inReverse = false; _braking = false;
            _customReverse = false; _requireThrottleRelease = true;
            _aircraftTelemetry.Reset();
            _log("模拟器已退出，等待重新连接。");
            return;
        }
        if (messageId == SimConnectApi.RECV_ID_EXCEPTION)
        {
            if (cbData < 20) return;
            _log($"SimConnect 异常代码：{Marshal.ReadInt32(pData, 12)}，发送序号：{Marshal.ReadInt32(pData, 16)}");
            return;
        }
        if ((uint)Marshal.ReadInt32(pData, SimConnectApi.RecvOffsetId)
            != SimConnectApi.RECV_ID_SIMOBJECT_DATA) return;
        if (cbData < 48) return;

        // 按 requestID 分派到对应字段
        uint requestId = (uint)Marshal.ReadInt32(pData, RecvOffsetRequestId);
        int index = (int)(requestId - RequestTelemetry);
        if (index < 0 || index >= TelemetryFieldCount) return;

        double value = BitConverter.Int64BitsToDouble(
            Marshal.ReadInt64(pData, SimConnectApi.RecvOffsetSimObjectData));
        if (!double.IsFinite(value)) return;

        lock (_telemetry)
        {
            _telemetry[index] = value;
            _telemetryTimes[index] = _clock();
        }
        Interlocked.Exchange(ref _lastTelemetryMs, Environment.TickCount64);
    }

    private const int RecvOffsetRequestId = 12;

    public void Dispose()
    {
        _cts.Cancel();
        _pumpTask?.GetAwaiter().GetResult();
        try
        {
            if (_handle != IntPtr.Zero)
            {
                ReleaseMomentary();
                SimConnectApi.SimConnect_Close(_handle);
            }
        }
        catch (Exception ex)
        {
            _log($"关闭模拟器连接失败：{ex.Message}");
        }
        _handle = IntPtr.Zero;
        _cts.Dispose();
    }
}

/// <summary>按键位掩码，与手机端 <c>Protocol</c> 的定义一一对应。</summary>
public static class ButtonMask
{
    public const ushort Gear = 1 << 0;
    public const ushort FlapsUp = 1 << 1;
    public const ushort FlapsDown = 1 << 2;
    public const ushort Autopilot = 1 << 3;
    public const ushort ParkingBrake = 1 << 4;
    public const ushort Pause = 1 << 5;
}
