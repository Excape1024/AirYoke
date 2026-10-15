namespace AirYoke.Bridge.Sim;

/// <summary>
/// 把手机发来的操纵量送到模拟器。
///
/// 抽成接口是为了让桥接程序在**没有安装 MSFS 的机器上也能完整跑通链路** ——
/// 阶段 3 先用 <see cref="DryRunOutput"/> 验证手机到 PC 这一段，
/// 阶段 4 再接上真正的 SimConnect 实现。
/// </summary>
public interface ISimOutput : IDisposable
{
    /// <summary>显示在状态行里的名字。</summary>
    string Name { get; }

    /// <summary>是否已经连上模拟器。</summary>
    bool IsConnected { get; }

    /// <summary>把一帧操纵量送出去。由 100Hz 的接收节奏驱动。</summary>
    void Apply(in Protocol.State state);

    /// <summary>读取要回传给手机的遥测。</summary>
    Protocol.Telemetry BuildTelemetry(int seq);
}

/// <summary>
/// 空转实现：只把收到的操纵量记录下来，不驱动任何模拟器。
///
/// 遥测**返回真零**而不是编造数据 —— 界面上看到 0 就说明"还没接模拟器"，
/// 比给个好看的正弦波更诚实，也不会让人误以为链路已经端到端打通。
/// </summary>
public sealed class DryRunOutput : ISimOutput
{
    private Protocol.State _latest;
    private long _frames;

    public string Name => "空转（未接模拟器）";
    public bool IsConnected => false;

    public Protocol.State Latest => _latest;
    public long Frames => Interlocked.Read(ref _frames);

    public void Apply(in Protocol.State state)
    {
        _latest = state;
        Interlocked.Increment(ref _frames);
    }

    public Protocol.Telemetry BuildTelemetry(int seq) => new(
        Seq: seq,
        AirspeedKt: 0f,
        AltitudeFt: 0f,
        HeadingDeg: 0f,
        PitchDeg: 0f,
        BankDeg: 0f,
        VerticalSpeedFpm: 0f,
        GearDown: false,
        FlapsIndex: 0,
        AutopilotOn: false,
        LinkQuality: 100,
        ThrottlePos: 0f,
        SpoilersPos: 0f,
        AutothrottleOn: false);

    public void Dispose()
    {
    }
}
