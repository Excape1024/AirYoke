using System.Text.RegularExpressions;

namespace AirYoke.Bridge.Sim;

/// <summary>
/// 找到 <c>SimConnect.dll</c>（原生 C API）。
///
/// 这个 DLL **随模拟器一起安装**，不需要单独装 SDK，也不需要随本程序分发 ——
/// 微软的运行时组件由模拟器自己带。所以这里只负责定位，不做拷贝。
///
/// 定位顺序（从最可靠到最兜底）：
///  1. 本程序目录旁边（万一用户手动拷了一份）
///  2. 模拟器自己记录的安装路径 —— `UserCfg.opt` 里的 `InstalledPackagesPath`
///  3. 常见盘符下的包目录
/// </summary>
public static class SimConnectLocator
{
    private const string DllName = "SimConnect.dll";

    private static string? _cached;

    /// <summary>找到的完整路径；找不到返回 null。</summary>
    public static string? Find()
    {
        if (_cached is not null && File.Exists(_cached)) return _cached;

        foreach (var candidate in Candidates())
        {
            try
            {
                if (File.Exists(candidate))
                {
                    _cached = candidate;
                    return candidate;
                }
            }
            catch
            {
                // 非法路径项，跳过
            }
        }
        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        // 1) 本程序旁边
        yield return Path.Combine(AppContext.BaseDirectory, DllName);
        yield return Path.Combine(AppContext.BaseDirectory, "SimConnect", DllName);

        // 2) 模拟器自己记录的包路径
        var packagesPath = ReadInstalledPackagesPath();
        if (!string.IsNullOrWhiteSpace(packagesPath))
        {
            yield return Path.Combine(packagesPath, "Content", DllName);
            yield return Path.Combine(packagesPath, DllName);
        }

        // 3) 常见盘符下的兜底位置
        foreach (var root in new[] { "D:\\xbox", "C:\\xbox", "D:\\", "C:\\" })
        {
            foreach (var dir in SafeEnumerateDirectories(root, "Microsoft Flight Simulator*"))
            {
                yield return Path.Combine(dir, "Content", DllName);
                yield return Path.Combine(dir, DllName);
            }
        }
    }

    /// <summary>
    /// 从 <c>UserCfg.opt</c> 读模拟器的包安装路径。
    ///
    /// 文件里有一行形如 `InstalledPackagesPath "D:\xbox\Microsoft Flight Simulator_1"`，
    /// 这是模拟器自己写下的权威路径，比自己猜盘符可靠得多。
    /// </summary>
    private static string? ReadInstalledPackagesPath()
    {
        var cfg = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft Flight Simulator", "UserCfg.opt");

        try
        {
            if (!File.Exists(cfg)) return null;

            foreach (var line in File.ReadLines(cfg))
            {
                var match = Regex.Match(line, @"InstalledPackagesPath\s+""(.+?)""");
                if (match.Success) return match.Groups[1].Value;
            }
        }
        catch
        {
            // 读不到就退回盘符枚举
        }
        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string pattern)
    {
        try
        {
            if (!Directory.Exists(root)) return Array.Empty<string>();
            return Directory.GetDirectories(root, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
