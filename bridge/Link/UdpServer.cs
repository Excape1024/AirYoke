using System.Net;
using System.Net.Sockets;

namespace AirYoke.Bridge.Link;

/// <summary>
/// 通道 A：USB 网络共享 + UDP 的 PC 侧。
///
/// 手机周期性广播 <see cref="Protocol.KindDiscover"/>，本服务收到后记下它的地址并回
/// <see cref="Protocol.KindBeacon"/>；同时自己也周期性地往手机的候选地址发 beacon，
/// 两条路都通，发现更快。
/// </summary>
public sealed class UdpServer : IDisposable
{
    private readonly Action<Protocol.State> _onState;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _cts = new();

    private UdpClient? _client;
    private IPEndPoint? _phoneEndPoint;
    private Task? _receiveTask;
    private Task? _beaconTask;

    public DateTime LastReceiveUtc { get; private set; } = DateTime.MinValue;
    public string RemoteDescription => _phoneEndPoint?.ToString() ?? "（未发现手机）";
    public TetherInterface.Info? Tether { get; private set; }
    public bool IsListening => _client is not null;

    public UdpServer(Action<Protocol.State> onState, Action<string> log)
    {
        _onState = onState;
        _log = log;
    }

    /// <summary>
    /// 开始监听。端口被占用等原因失败时返回 false，并给出可操作的提示 ——
    /// 最常见的场景是用户双击了两次 exe。
    /// </summary>
    public bool Start()
    {
        try
        {
            _client = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.Port))
            {
                EnableBroadcast = true,
            };
        }
        catch (SocketException e)
        {
            _log($"UDP 端口 {Protocol.Port} 无法监听：{DescribeSocketError(e)}");
            return false;
        }

        Tether = TetherInterface.Find();
        if (Tether is { } info)
        {
            _log($"检测到 USB 网络共享网卡：{info.InterfaceName} 本机 {info.LocalAddress}");
        }
        else
        {
            _log("未检测到 USB 网络共享网卡（手机没插，或没开「USB 网络共享」）。" +
                 "UDP 通道暂时只能等手机主动广播。");
        }

        _receiveTask = Task.Run(ReceiveLoop);
        _beaconTask = Task.Run(BeaconLoop);
        return true;
    }

    private async Task ReceiveLoop()
    {
        var client = _client;
        if (client is null) return;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await client.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                LastReceiveUtc = DateTime.UtcNow;

                // 记下来源地址：这就是手机，后面所有回包都发这里
                if (_phoneEndPoint is null || !_phoneEndPoint.Equals(result.RemoteEndPoint))
                {
                    _phoneEndPoint = result.RemoteEndPoint;
                    _log($"手机已连接：{result.RemoteEndPoint}");
                }

                HandleDatagram(result.Buffer, result.RemoteEndPoint);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException e)
            {
                // Windows 上收到 ICMP 端口不可达会让 UDP 接收抛错，这不是致命问题
                if (!_cts.IsCancellationRequested)
                    _log($"UDP 接收异常：{e.SocketErrorCode}");
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested)
                    _log($"UDP 接收异常：{e.Message}");
            }
        }
    }

    /// <summary>
    /// 处理一个数据报。
    ///
    /// 单独拆成同步方法，是因为 <c>Span&lt;byte&gt;</c> 是 ref struct，
    /// 不能出现在 async 方法里（编译期直接报错）。
    /// </summary>
    private void HandleDatagram(byte[] data, IPEndPoint from)
    {
        ReadOnlySpan<byte> span = data;

        byte kind = Protocol.DecodeHandshake(span);
        if (kind == Protocol.KindDiscover)
        {
            // 手机在找我们，立刻回 beacon，不用等下一个周期
            SendBeacon(from);
            return;
        }
        if (kind == Protocol.KindBeacon) return; // 我们只发不收

        if (Protocol.TryDecodeState(span, out var state))
        {
            _onState(state);
        }
    }

    private async Task BeaconLoop()
    {
        var packet = new byte[Protocol.HandshakeSize];
        Protocol.EncodeHandshake(Protocol.KindBeacon, packet);

        while (!_cts.IsCancellationRequested)
        {
            var client = _client;
            if (client is not null)
            {
                try
                {
                    var phone = _phoneEndPoint;
                    if (phone is not null)
                    {
                        client.Send(packet, packet.Length, phone);
                    }
                    else
                    {
                        // 还不知道手机在哪，往候选地址都发一遍
                        foreach (var candidate in TetherInterface.PhoneCandidates(Tether))
                        {
                            try
                            {
                                client.Send(packet, packet.Length,
                                    new IPEndPoint(candidate, Protocol.Port));
                            }
                            catch
                            {
                                // 单个候选地址不可达很正常，继续试下一个
                            }
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception e)
                {
                    if (!_cts.IsCancellationRequested) _log($"UDP beacon 发送失败：{e.Message}");
                }
            }

            try
            {
                await Task.Delay(1000, _cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void SendBeacon(IPEndPoint target)
    {
        var client = _client;
        if (client is null) return;

        var packet = new byte[Protocol.HandshakeSize];
        Protocol.EncodeHandshake(Protocol.KindBeacon, packet);
        try
        {
            client.Send(packet, packet.Length, target);
        }
        catch
        {
            // beacon 是尽力而为的，失败就等下一轮
        }
    }

    /// <summary>回传遥测给手机显示。</summary>
    public void SendTelemetry(in Protocol.Telemetry telemetry)
    {
        var client = _client;
        var target = _phoneEndPoint;
        if (client is null || target is null) return;

        var packet = new byte[Protocol.TelemetrySize];
        Protocol.EncodeTelemetry(telemetry, packet);
        try
        {
            client.Send(packet, packet.Length, target);
        }
        catch
        {
            // 遥测是锦上添花，丢了不影响操纵
        }
    }

    /// <summary>把 SocketException 翻译成用户看得懂的话。</summary>
    private static string DescribeSocketError(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.AddressAlreadyInUse =>
            "端口已被占用。多半是已经有一个桥接程序在运行了 —— 检查任务栏或任务管理器。",
        SocketError.AccessDenied =>
            "权限不足。换个端口，或以管理员身份运行。",
        _ => e.Message,
    };

    public void Dispose()
    {
        _cts.Cancel();
        _client?.Dispose();
        _client = null;
        _cts.Dispose();
    }
}
