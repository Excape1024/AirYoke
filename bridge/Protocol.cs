using System.Buffers.Binary;

namespace AirYoke.Bridge;

/// <summary>
/// 手机 ↔ PC 的二进制协议。**这是 <c>app/src/main/java/com/airyoke/link/Protocol.kt</c> 的镜像。**
///
/// 两边必须逐字节一致。任何一侧改了布局而另一侧没跟上，表现是飞机乱动而非报错，
/// 极难排查 —— 所以修改这里时务必同步改 Kotlin 那边，并跑
/// <c>ProtocolTest</c>（Kotlin 侧用标准 CRC 向量对照过，C# 侧用同一向量自测）。
///
/// 全部小端序。轴量用 i16 承载 −32767..32767。
/// </summary>
public static class Protocol
{
    public const ushort Magic = 0xA1C0;

    /// <summary>协议四：启用辅助控制位和遥测状态位，包长度不变。</summary>
    public const byte Version = 4;
    /// <summary>状态包按键 bit6：专用自动驾驶断开请求。</summary>
    public const ushort ButtonApDisconnect = 0x40;
    /// <summary>遥测状态 bit3：支持专用自动驾驶断开请求。</summary>
    public const byte SystemApDisconnectSupported = 0x08;
    public const int Port = 9000;

    public const int StateSize = 26;
    public const int TelemetrySize = 47;
    public const int HandshakeSize = 6;

    // 状态包 flags 位
    public const byte FlagCalibrated = 0x01;
    public const byte FlagLinkProbe = 0x02;

    /// <summary>手机接管推力或正在交还 CL；正推力松手仍保持。</summary>
    public const byte FlagThrottleEngaged = 0x04;

    // 握手包 kind
    public const byte KindDiscover = 1;
    public const byte KindBeacon = 2;

    public const float AxisMax = 32767f;

    /// <summary>手机发来的一帧操纵状态。</summary>
    public readonly record struct State(
        int Seq,
        byte Flags,
        short Elevator,
        short Aileron,
        short Rudder,
        short Throttle,
        ushort Buttons,
        ushort Aux,
        int TimestampMs)
    {
        public bool Calibrated => (Flags & FlagCalibrated) != 0;

        /// <summary>手机是否正在写入推力目标。见 <see cref="FlagThrottleEngaged"/>。</summary>
        public bool ThrottleEngaged => (Flags & FlagThrottleEngaged) != 0;

        // 归一化到 −1..1，交给操纵输出层
        public float ElevatorNorm => Elevator / AxisMax;
        public float AileronNorm => Aileron / AxisMax;
        public float RudderNorm => Rudder / AxisMax;
        public float ThrottleNorm => Throttle / AxisMax;
    }

    /// <summary>桥接程序回传给手机显示的飞行数据。</summary>
    public readonly record struct Telemetry(
        int Seq,
        float AirspeedKt,
        float AltitudeFt,
        float HeadingDeg,
        float PitchDeg,
        float BankDeg,
        float VerticalSpeedFpm,
        bool GearDown,
        int FlapsIndex,
        bool AutopilotOn,
        int LinkQuality,
        /// <summary>模拟器里的实际推力手柄位置，0 = 慢车、1 = 满推力。</summary>
        float ThrottlePos,
        /// <summary>减速板手柄位置 0..1。</summary>
        float SpoilersPos,
        /// <summary>自动油门接通状态，仅显示，不用于学习 CL。</summary>
        bool AutothrottleOn,
        byte SystemFlags = 0);

    // ---------------- 状态包 ----------------

    public static int EncodeState(in State s, Span<byte> dst)
    {
        if (dst.Length < StateSize) throw new ArgumentException("缓冲区太小", nameof(dst));

        BinaryPrimitives.WriteUInt16LittleEndian(dst[..2], Magic);
        dst[2] = Version;
        dst[3] = s.Flags;
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4, 4), s.Seq);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(8, 2), s.Elevator);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(10, 2), s.Aileron);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(12, 2), s.Rudder);
        BinaryPrimitives.WriteInt16LittleEndian(dst.Slice(14, 2), s.Throttle);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(16, 2), s.Buttons);
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(18, 2), s.Aux);
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(20, 4), s.TimestampMs);
        BinaryPrimitives.WriteUInt16LittleEndian(
            dst.Slice(24, 2), Crc16(dst[..24]));

        return StateSize;
    }

    /// <summary>
    /// 解码状态包。任何校验不过都返回 false —— 宁可不动作，
    /// 也不要把垃圾数据当成舵量送进飞机。
    /// </summary>
    public static bool TryDecodeState(ReadOnlySpan<byte> src, out State state)
    {
        state = default;
        if (src.Length < StateSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src[..2]) != Magic) return false;
        if (src[2] != Version) return false;

        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(StateSize - 2, 2));
        if (expected != Crc16(src[..(StateSize - 2)])) return false;

        state = new State(
            Seq: BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4, 4)),
            Flags: src[3],
            Elevator: BinaryPrimitives.ReadInt16LittleEndian(src.Slice(8, 2)),
            Aileron: BinaryPrimitives.ReadInt16LittleEndian(src.Slice(10, 2)),
            Rudder: BinaryPrimitives.ReadInt16LittleEndian(src.Slice(12, 2)),
            Throttle: BinaryPrimitives.ReadInt16LittleEndian(src.Slice(14, 2)),
            Buttons: BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(16, 2)),
            Aux: BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(18, 2)),
            TimestampMs: BinaryPrimitives.ReadInt32LittleEndian(src.Slice(20, 4)));

        return true;
    }

    // ---------------- 遥测包 ----------------

    public static int EncodeTelemetry(in Telemetry t, Span<byte> dst)
    {
        if (dst.Length < TelemetrySize) throw new ArgumentException("缓冲区太小", nameof(dst));

        BinaryPrimitives.WriteUInt16LittleEndian(dst[..2], Magic);
        dst[2] = Version;
        dst[3] = t.SystemFlags;
        BinaryPrimitives.WriteInt32LittleEndian(dst.Slice(4, 4), t.Seq);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(8, 4), t.AirspeedKt);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(12, 4), t.AltitudeFt);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(16, 4), t.HeadingDeg);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(20, 4), t.PitchDeg);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(24, 4), t.BankDeg);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(28, 4), t.VerticalSpeedFpm);
        dst[32] = (byte)(t.GearDown ? 1 : 0);
        dst[33] = (byte)t.FlapsIndex;
        dst[34] = (byte)(t.AutopilotOn ? 1 : 0);
        dst[35] = (byte)t.LinkQuality;
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(36, 4), t.ThrottlePos);
        BinaryPrimitives.WriteSingleLittleEndian(dst.Slice(40, 4), t.SpoilersPos);
        dst[44] = (byte)(t.AutothrottleOn ? 1 : 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            dst.Slice(45, 2), Crc16(dst[..45]));

        return TelemetrySize;
    }

    public static bool TryDecodeTelemetry(ReadOnlySpan<byte> src, out Telemetry telemetry)
    {
        telemetry = default;
        if (src.Length < TelemetrySize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src[..2]) != Magic) return false;
        if (src[2] != Version) return false;

        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(TelemetrySize - 2, 2));
        if (expected != Crc16(src[..(TelemetrySize - 2)])) return false;

        telemetry = new Telemetry(
            Seq: BinaryPrimitives.ReadInt32LittleEndian(src.Slice(4, 4)),
            AirspeedKt: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(8, 4)),
            AltitudeFt: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(12, 4)),
            HeadingDeg: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(16, 4)),
            PitchDeg: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(20, 4)),
            BankDeg: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(24, 4)),
            VerticalSpeedFpm: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(28, 4)),
            GearDown: src[32] != 0,
            FlapsIndex: src[33],
            AutopilotOn: src[34] != 0,
            LinkQuality: src[35],
            ThrottlePos: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(36, 4)),
            SpoilersPos: BinaryPrimitives.ReadSingleLittleEndian(src.Slice(40, 4)),
            AutothrottleOn: src[44] != 0,
            SystemFlags: src[3]);

        return true;
    }

    // ---------------- 发现/握手包 ----------------

    public static int EncodeHandshake(byte kind, Span<byte> dst)
    {
        if (dst.Length < HandshakeSize) throw new ArgumentException("缓冲区太小", nameof(dst));

        BinaryPrimitives.WriteUInt16LittleEndian(dst[..2], Magic);
        dst[2] = Version;
        dst[3] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(4, 2), Crc16(dst[..4]));

        return HandshakeSize;
    }

    /// <summary>返回 kind，非法包返回 0。</summary>
    public static byte DecodeHandshake(ReadOnlySpan<byte> src)
    {
        if (src.Length < HandshakeSize) return 0;
        if (BinaryPrimitives.ReadUInt16LittleEndian(src[..2]) != Magic) return 0;
        if (src[2] != Version) return 0;

        ushort expected = BinaryPrimitives.ReadUInt16LittleEndian(src.Slice(HandshakeSize - 2, 2));
        if (expected != Crc16(src[..(HandshakeSize - 2)])) return 0;

        return src[3];
    }

    // ---------------- CRC ----------------

    /// <summary>
    /// CRC-16/CCITT-FALSE，多项式 0x1021，初值 0xFFFF。
    /// 标准测试向量："123456789" → 0x29B1。
    /// </summary>
    public static ushort Crc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
        {
            crc ^= (ushort)(b << 8);
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x8000) != 0
                    ? (ushort)((crc << 1) ^ 0x1021)
                    : (ushort)(crc << 1);
            }
        }
        return crc;
    }
}

