using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace AirYoke.Bridge.Link;

/// <summary>
/// 找出 PC 上那条「手机 USB 网络共享」的虚拟网卡。
///
/// ## 为什么不能按网段找
///
/// 早期 Android 把 USB 网络共享固定建在 <c>192.168.42.0/24</c> 上，手机自己是
/// <c>192.168.42.129</c>。**Android 12 起不再如此** —— 每次开启网络共享会随机挑一个
/// 私有网段（实测一台 Galaxy S25 用的是 <c>10.24.237.0/24</c>）。
///
/// 按 <c>192.168.42.</c> 前缀匹配的后果很隐蔽：一个网卡都匹配不上，于是这条"网线"
/// 被判定为不存在，beacon 全发到不存在的地址上。链路其实好得很，只是没人知道它在哪。
///
/// 所以改成按网卡的**描述**认：Android 手机通过 RNDIS / NCM 协议共享网络，
/// Windows 会把它枚举成 "Remote NDIS based Internet Sharing Device" 这类设备。
///
/// ## 找不到也不是致命的
///
/// 这个方法只影响 PC **主动**发 beacon。手机自己会周期性广播发现包，
/// 而 [UdpServer] 绑定在 <c>0.0.0.0</c> 上，收到谁的包就往谁的源地址回 ——
/// 所以即使这里返回 null，双向发现照样能建立，只是慢一个心跳。
/// </summary>
public static class TetherInterface
{
    /// <summary>
    /// USB 网络共享网卡在 Windows 描述里会出现的字样。
    ///
    /// 故意写得宽：不同厂商、不同 Windows 版本对同一类设备的命名并不统一，
    /// 而漏认的代价（发现慢一拍）比误认的代价（往无关网卡广播）高。
    /// </summary>
    private static readonly string[] TetherKeywords =
    {
        "Remote NDIS", "RNDIS", "NCM", "Internet Sharing", "Tethering",
    };

    public readonly record struct Info(IPAddress LocalAddress, IPAddress Broadcast, string InterfaceName);

    /// <summary>找到那条网卡；没插手机或没开网络共享时返回 null。</summary>
    public static Info? Find()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                if (!LooksLikeTether(nic)) continue;

                foreach (var addr in nic.GetIPProperties().UnicastAddresses)
                {
                    var ip = addr.Address;
                    if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (IPAddress.IsLoopback(ip)) continue;

                    // 由 IP 与掩码算出子网广播地址
                    var broadcast = ComputeBroadcast(ip, addr.IPv4Mask);
                    if (broadcast is null) continue;

                    return new Info(ip, broadcast, nic.Name);
                }
            }
        }
        catch
        {
            // 枚举网卡失败不是致命问题，调用方会退回"等手机广播"
        }
        return null;
    }

    private static bool LooksLikeTether(NetworkInterface nic)
    {
        // 名称和描述都要看：有的系统上描述是空的，有的反而是名称更有信息量
        string haystack = nic.Name + " " + nic.Description;
        foreach (var keyword in TetherKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>掩码缺失或非法时返回 null —— 宁可不用，也不要编一个广播地址出来。</summary>
    private static IPAddress? ComputeBroadcast(IPAddress ip, IPAddress? mask)
    {
        if (mask is null) return null;

        var ipBytes = ip.GetAddressBytes();
        var maskBytes = mask.GetAddressBytes();
        if (ipBytes.Length != 4 || maskBytes.Length != 4) return null;

        var result = new byte[4];
        for (int i = 0; i < 4; i++)
        {
            result[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 0xFF));
        }
        return new IPAddress(result);
    }

    /// <summary>
    /// 往哪些地址发 beacon 才能让手机收到。
    ///
    /// 手机的地址由手机的 DHCP 分配，PC 这边**无法预先知道**（网段也是随机的），
    /// 所以只能发子网广播 —— 手机会收到，然后照着我们 beacon 的源地址回包。
    /// </summary>
    public static IEnumerable<IPAddress> PhoneCandidates(Info? info)
    {
        if (info is { } found) yield return found.Broadcast;
    }
}
