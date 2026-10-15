using System.Net;
using System.Net.Sockets;

namespace AirYoke.Bridge.Link;

/// <summary>
/// 通道 B：adb reverse + TCP 的 PC 侧。
///
/// PC 执行 <c>adb reverse tcp:9000 tcp:9000</c> 后，手机连它**自己的** 127.0.0.1:9000
/// 会被转发到这里。所以本服务只需监听回环地址 —— 它从不接受来自局域网或互联网的连接，
/// 这既是安全边界，也意味着不需要处理外部地址。
/// </summary>
public sealed class TcpServer : IDisposable
{
    private readonly Action<Protocol.State> _onState;
    private readonly Action<string> _log;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    private TcpClient? _client;
    private NetworkStream? _stream;
    private Task? _acceptTask;

    public DateTime LastReceiveUtc { get; private set; } = DateTime.MinValue;
    public bool Connected => _client?.Connected == true;
    public string RemoteDescription => Connected ? "127.0.0.1（adb reverse）" : "（未连接）";

    private const int ReadBufferSize = 4096;

    /// <summary>累积缓冲能兜住多少帧。够吸收一次调度抖动即可。</summary>
    private const int PendingFrames = 64;

    public TcpServer(Action<Protocol.State> onState, Action<string> log)
    {
        _onState = onState;
        _log = log;
        _listener = new TcpListener(IPAddress.Loopback, Protocol.Port);
    }

    /// <summary>开始监听。失败返回 false（通常是端口被占用）。</summary>
    public bool Start()
    {
        try
        {
            _listener.Start();
            _acceptTask = Task.Run(AcceptLoop);
            return true;
        }
        catch (SocketException e)
        {
            string reason = e.SocketErrorCode == SocketError.AddressAlreadyInUse
                ? "端口已被占用，多半是已经有一个桥接程序在运行了"
                : e.Message;
            _log($"TCP 端口 {Protocol.Port} 无法监听：{reason}");
            return false;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);

                // 手机侧每 10ms 一帧，Nagle 会把小包攒起来一起发，造成明显的延迟抖动
                client.NoDelay = true;

                // 只保留最新一条连接：重连时旧连接必然已失效，留着只会产生僵尸流
                CloseClient();

                _client = client;
                _stream = client.GetStream();
                LastReceiveUtc = DateTime.UtcNow;
                _log("手机已通过 adb reverse 连接");

                _ = Task.Run(() => ReceiveLoop(client, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                if (!_cts.IsCancellationRequested) _log($"TCP 接受连接异常：{e.Message}");
            }
        }
    }

    private void ReceiveLoop(TcpClient client, CancellationToken token)
    {
        var buffer = new byte[ReadBufferSize];

        // 定长累积缓冲。用裸数组而不是 List<byte>：List 每切一个包都要
        // ToArray() 复制整段未消费数据，在 100Hz 下是白白的内存流量。
        var pending = new byte[Protocol.StateSize * PendingFrames];
        int pendingCount = 0;

        try
        {
            while (!token.IsCancellationRequested && client.Connected)
            {
                int read = client.GetStream().Read(buffer, 0, buffer.Length);
                if (read <= 0) break;

                LastReceiveUtc = DateTime.UtcNow;

                // 缓冲满说明对端发了我们解不出的东西或严重滞后。
                // 丢掉最旧的、保住最新的 —— 操纵数据只有最新一帧有意义。
                if (pendingCount + read > pending.Length)
                {
                    int keep = Math.Min(pendingCount, Protocol.StateSize - 1);
                    Array.Copy(pending, pendingCount - keep, pending, 0, keep);
                    pendingCount = keep;
                }

                Buffer.BlockCopy(buffer, 0, pending, pendingCount, read);
                pendingCount += read;

                // TCP 是字节流，没有消息边界，得自己按定长把包切出来
                int offset = 0;
                while (pendingCount - offset >= Protocol.StateSize)
                {
                    if (Protocol.TryDecodeState(
                            new ReadOnlySpan<byte>(pending, offset, Protocol.StateSize),
                            out var state))
                    {
                        _onState(state);
                    }
                    // 解不出来说明流已经错位。丢掉一个包的宽度继续往前找对齐点，
                    // 而不是整段清空 —— 后者会在噪声下反复丢光所有数据。
                    offset += Protocol.StateSize;
                }

                // 把没消费完的尾巴挪回开头
                if (offset > 0)
                {
                    pendingCount -= offset;
                    if (pendingCount > 0)
                    {
                        Array.Copy(pending, offset, pending, 0, pendingCount);
                    }
                }
            }
        }
        catch (Exception e)
        {
            if (!token.IsCancellationRequested) _log($"TCP 接收异常：{e.Message}");
        }
        finally
        {
            // 只有这条连接仍是"当前连接"时才清理。
            //
            // 否则会有这样一个竞态：新连接已被 AcceptLoop 接手并写进 _client/_stream，
            // 而旧连接的接收循环这时才走到 finally，一把 CloseClient() 把**新连接**关掉。
            // 症状是"连上 1.5 秒后又断"，且反复重连。
            if (ReferenceEquals(_client, client))
            {
                _log("手机 TCP 连接已断开");
                CloseClient();
            }
        }
    }

    public void SendTelemetry(in Protocol.Telemetry telemetry)
    {
        var stream = _stream;
        if (stream is null) return;

        var packet = new byte[Protocol.TelemetrySize];
        Protocol.EncodeTelemetry(telemetry, packet);
        try
        {
            stream.Write(packet, 0, packet.Length);
            stream.Flush();
        }
        catch
        {
            CloseClient();
        }
    }

    private void CloseClient()
    {
        try { _client?.Close(); } catch { }
        _client = null;
        _stream = null;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        CloseClient();
        _cts.Dispose();
    }
}
