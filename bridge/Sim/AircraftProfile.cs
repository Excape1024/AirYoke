namespace AirYoke.Bridge.Sim;

/// <summary>机模身份必须与实际加载的飞机吻合，禁止把未定义的局部变量零值当成反馈。</summary>
internal static class AircraftProfile
{
    public const byte Asobo = 0, IniBuilds = 1, FlyByWire = 2, Unknown = 255;

    public static byte Identify(string title)
    {
        if (title.Contains("FlyByWire_A320_NEO", StringComparison.OrdinalIgnoreCase)) return FlyByWire;
        if (title.Contains("microsoft-aircraft-a320neo", StringComparison.OrdinalIgnoreCase)) return IniBuilds;
        if (title.Contains("Asobo_A320_NEO", StringComparison.OrdinalIgnoreCase)) return Asobo;
        if (title.Contains("FlyByWire", StringComparison.OrdinalIgnoreCase) &&
            (title.Contains("A320", StringComparison.OrdinalIgnoreCase) || title.Contains("A32NX", StringComparison.OrdinalIgnoreCase)))
            return FlyByWire;
        if ((title.Contains("iniBuilds", StringComparison.OrdinalIgnoreCase) || title.Contains("A320neo LEAP", StringComparison.OrdinalIgnoreCase)) &&
            title.Contains("A320", StringComparison.OrdinalIgnoreCase)) return IniBuilds;
        if (title.StartsWith("Airbus A320 Neo", StringComparison.OrdinalIgnoreCase)) return Asobo;
        return Unknown;
    }

    public static string Prefix(byte profile) => profile == FlyByWire ? "A32NX" : "INI";

    /// <summary>两款空客都以杆角为协议语义：慢车零，CL 为 25/45，FLX 为 35/45。</summary>
    public static float LeverFromAngle(double angle) => (float)(angle < 0 ? angle / 20 : angle / 45);

    public static bool ValidAngle(double angle) => double.IsFinite(angle) && angle >= -20 && angle <= 45;

    /// <summary>读取每根轴自己的卡位边界，映射到卡位中心，禁止沿用另一机型的固定百分比。</summary>
    public static bool TryMapAxis(float lever, ReadOnlySpan<double> bounds, bool reverseOnAxis, out int data)
    {
        data = 0;
        if (!float.IsFinite(lever) || bounds.Length != 12) return false;
        // 顺序：全反推、反推慢车、慢车、CL、FLX、TOGA；各自为下界、上界。
        Span<double> centers = stackalloc double[6];
        int first = reverseOnAxis ? 0 : 2;
        for (int i = first; i < 6; i++)
        {
            double low = bounds[i * 2], high = bounds[i * 2 + 1];
            if (!double.IsFinite(low) || !double.IsFinite(high) || low < -1 || high > 1 || low > high) return false;
            centers[i] = (low + high) / 2;
            if (i > first && (centers[i] <= centers[i - 1] || low < bounds[(i - 1) * 2 + 1])) return false;
        }
        if (lever < 0 && !reverseOnAxis) return false;
        ReadOnlySpan<double> stops = [-1, -.3, 0, 25.0 / 45, 35.0 / 45, 1];
        double target = Math.Clamp(lever, -1, 1);
        // 协议量化不能把恰好在卡位的指令挤到卡位外侧。
        for (int i = first; i < 6; i++)
            if (Math.Abs(target - stops[i]) < .0001) { data = ToAxis(centers[i]); return true; }
        for (int i = first + 1; i < 6; i++)
        {
            if (target > stops[i]) continue;
            double fraction = (target - stops[i - 1]) / (stops[i] - stops[i - 1]);
            // 卡位之间使用两个卡位的外侧边界，避免扩大吸附区。
            double axis = bounds[(i - 1) * 2 + 1] + fraction * (bounds[i * 2] - bounds[(i - 1) * 2 + 1]);
            data = ToAxis(axis);
            return true;
        }
        return false;
    }

    private static int ToAxis(double axis) => Math.Clamp((int)Math.Round(axis * 16384), -16383, 16384);
}
