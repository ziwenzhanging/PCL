using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using EasyTierProto.Api.Instance;
using EasyTierProto.Common;
using Google.Protobuf;

namespace PCLCS.EasyTier;

/// <summary>
/// EasyTier 核心 <c>--rpc-portal</c> 的最小客户端。
/// <para>
/// 该端口并非 gRPC，而是 EasyTier 私有 RPC：TCP 上传输
/// <c>[u32 小端总长][16 字节 PeerManagerHeader][RpcPacket(protobuf)]</c>。
/// </para>
/// <para>
/// <c>method_index</c> 由 easytier-proto/build/rpc.rs 生成，规则是「proto 中方法声明顺序 + 1」，
/// 因此是稳定的常量，不随版本随机变化。
/// </para>
/// <para>
/// JSON 采用手工拼接，不依赖任何第三方序列化库，避免宿主程序集版本差异导致加载失败。
/// </para>
/// </summary>
public sealed class EasyTierRpcClient : IDisposable
{
    /// <summary>管理 RPC 的服务名。</summary>
    public const string ServiceName = "PeerManageRpc";

    /// <summary>ListPeer 的方法索引（PeerManageRpc 中第 1 个方法）。</summary>
    public const uint MethodListPeer = 1;

    /// <summary>ListRoute 的方法索引（PeerManageRpc 中第 3 个方法）。</summary>
    public const uint MethodListRoute = 3;

    /// <summary>ShowNodeInfo 的方法索引（PeerManageRpc 中第 7 个方法）。</summary>
    public const uint MethodShowNodeInfo = 7;

    private const byte PacketTypeRpcReq = 8;
    private const byte PacketTypeRpcResp = 9;
    private const int PeerManagerHeaderSize = 16;
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private long _transactionId;

    public EasyTierRpcClient(string host, int port, int timeoutMs = 3000)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _port = port;
        _timeoutMs = timeoutMs;
        _transactionId = DateTime.UtcNow.Ticks & 0x7FFFFFFF;
    }

    /// <summary>
    /// 读取本机节点信息。失败时抛出异常。
    /// </summary>
    public NodeInfo GetNodeInfo(string? instanceName = null)
    {
        var request = new ShowNodeInfoRequest { Instance = BuildInstance(instanceName) };
        var payload = Invoke(MethodShowNodeInfo, request);
        return ShowNodeInfoResponse.Parser.ParseFrom(payload).NodeInfo;
    }

    /// <summary>
    /// 读取本机节点信息并以 JSON 返回，字段名与 easytier-cli 一致（snake_case）。
    /// </summary>
    public string GetNodeInfoJson(string? instanceName = null)
    {
        var info = GetNodeInfo(instanceName);
        var listeners = string.Join(",", info.Listeners.Select(JsonString));
        return "{" +
               "\"ipv4_addr\":" + JsonString(info.Ipv4Addr ?? "") + "," +
               "\"hostname\":" + JsonString(info.Hostname ?? "") + "," +
               "\"version\":" + JsonString(info.Version ?? "") + "," +
               "\"inst_id\":" + JsonString(info.InstId ?? "") + "," +
               "\"peer_id\":" + info.PeerId + "," +
               "\"listeners\":[" + listeners + "]," +
               "\"stun_info\":{" +
                   "\"udp_nat_type\":" + JsonString(info.StunInfo?.UdpNatType.ToString() ?? "") + "," +
                   "\"tcp_nat_type\":" + JsonString(info.StunInfo?.TcpNatType.ToString() ?? "") +
               "}" +
               "}";
    }

    /// <summary>
    /// 读取对等节点列表。失败时抛出异常。
    /// </summary>
    public ListPeerResponse ListPeer(string? instanceName = null)
    {
        var request = new ListPeerRequest { Instance = BuildInstance(instanceName) };
        var payload = Invoke(MethodListPeer, request);
        return ListPeerResponse.Parser.ParseFrom(payload);
    }

    /// <summary>
    /// 读取路由表。失败时抛出异常。
    /// </summary>
    public ListRouteResponse ListRoute(string? instanceName = null)
    {
        var request = new ListRouteRequest { Instance = BuildInstance(instanceName) };
        var payload = Invoke(MethodListRoute, request);
        return ListRouteResponse.Parser.ParseFrom(payload);
    }

    /// <summary>
    /// 完整读取节点列表并以 JSON 数组返回，字段与 <c>easytier-cli peer -o json</c> 保持一致。
    /// <para>
    /// 组合方式与官方 CLI 相同：本机用 ShowNodeInfo，其余用 ListRoute 与 ListPeer 按 peer_id 配对
    /// （hostname / 虚拟地址 / 版本只在 Route 里，连接质量只在 PeerInfo 里）。
    /// </para>
    /// </summary>
    public string ListPeersJson(string? instanceName = null)
    {
        var nodeInfo = GetNodeInfo(instanceName);
        var peers = ListPeer(instanceName).PeerInfos;
        var routes = ListRoute(instanceName).Routes;

        var entries = new List<string> { BuildSelfEntry(nodeInfo) };
        foreach (var route in routes)
        {
            var peer = peers.FirstOrDefault(p => p.PeerId == route.PeerId);
            entries.Add(BuildPeerEntry(route, peer));
        }
        return "[" + string.Join(",", entries) + "]";
    }

    /// <summary>
    /// 本机条目：对应 CLI 里 cost 固定为 Local 的那一行。
    /// </summary>
    private static string BuildSelfEntry(NodeInfo info)
    {
        var cidr = info.Ipv4Addr ?? "";
        var ipv4 = cidr.Split('/')[0];
        return "{" +
               "\"cidr\":" + JsonString(cidr) + "," +
               "\"ipv4\":" + JsonString(ipv4) + "," +
               "\"hostname\":" + JsonString(info.Hostname ?? "") + "," +
               "\"cost\":\"Local\"," +
               "\"lat_ms\":\"-\"," +
               "\"loss_rate\":\"-\"," +
               "\"rx_bytes\":\"-\"," +
               "\"tx_bytes\":\"-\"," +
               "\"tunnel_proto\":\"-\"," +
               "\"nat_type\":" + JsonString(info.StunInfo?.UdpNatType.ToString() ?? "Unknown") + "," +
               "\"id\":" + JsonString(info.PeerId.ToString()) + "," +
               "\"version\":" + JsonString(info.Version ?? "") +
               "}";
    }

    /// <summary>
    /// 远端条目：Route 提供身份信息，PeerInfo 提供连接质量。
    /// </summary>
    private static string BuildPeerEntry(Route route, PeerInfo? peer)
    {
        // 与官方 CLI 一致：cost == 1 表示直连，取连接延迟；否则取自路由延迟，cost 文案为 relay(N)
        var latency = route.Cost == 1 ? GetPeerLatencyMs(peer) : route.PathLatencyLatencyFirst;
        var costText = route.Cost == 1 ? "p2p" : $"relay({route.Cost})";
        var version = string.IsNullOrEmpty(route.Version) ? "unknown" : route.Version;

        return "{" +
               "\"cidr\":" + JsonString(FormatCidr(route.Ipv4Addr)) + "," +
               "\"ipv4\":" + JsonString(FormatIpv4(route.Ipv4Addr)) + "," +
               "\"hostname\":" + JsonString(route.Hostname ?? "") + "," +
               "\"cost\":" + JsonString(costText) + "," +
               "\"lat_ms\":" + JsonString(latency.ToString("F2")) + "," +
               "\"loss_rate\":" + JsonString(GetLossRate(peer)) + "," +
               "\"rx_bytes\":" + JsonString(GetStatsText(peer, true)) + "," +
               "\"tx_bytes\":" + JsonString(GetStatsText(peer, false)) + "," +
               "\"tunnel_proto\":" + JsonString(GetTunnelProtos(peer)) + "," +
               "\"nat_type\":" + JsonString(route.StunInfo?.UdpNatType.ToString() ?? "Unknown") + "," +
               "\"id\":" + JsonString(route.PeerId.ToString()) + "," +
               "\"version\":" + JsonString(version) +
               "}";
    }

    /// <summary>
    /// 把字符串转成 JSON 字面量（含必要的转义）。
    /// </summary>
    private static string JsonString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        var builder = new StringBuilder(value!.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (c < ' ')
                        builder.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        builder.Append(c);
                    break;
            }
        }
        builder.Append('"');
        return builder.ToString();
    }

    private static double GetPeerLatencyMs(PeerInfo? peer)
    {
        if (peer is null) return 0;
        var values = peer.Conns
            .Where(c => c.Stats is not null && c.Stats.LatencyUs > 0)
            .Select(c => c.Stats.LatencyUs / 1000.0)
            .ToList();
        return values.Count == 0 ? 0 : values.Min();
    }

    private static string GetLossRate(PeerInfo? peer)
    {
        if (peer is null) return "-";
        var values = peer.Conns.Where(c => c.LossRate > 0).Select(c => c.LossRate).ToList();
        if (values.Count == 0) return "0.0%";
        return $"{values.Max() * 100:F1}%";
    }

    private static string GetStatsText(PeerInfo? peer, bool receive)
    {
        var bytes = 0L;
        if (peer is not null)
        {
            foreach (var conn in peer.Conns)
            {
                if (conn.Stats is null) continue;
                bytes += (long)(receive ? conn.Stats.RxBytes : conn.Stats.TxBytes);
            }
        }
        return FormatSize(bytes);
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1)
        {
            value /= 1000;
            unit++;
        }
        return unit == 0 ? $"{value:F0} {units[unit]}" : $"{value:F2} {units[unit]}";
    }

    private static string GetTunnelProtos(PeerInfo? peer)
    {
        if (peer is null) return "-";
        var protos = peer.Conns
            .Select(c => c.Tunnel?.TunnelType ?? "")
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();
        return protos.Count == 0 ? "-" : string.Join(",", protos);
    }

    /// <summary>
    /// 把 Ipv4Inet 格式化为 a.b.c.d/n；无地址时返回空串。
    /// </summary>
    private static string FormatCidr(Ipv4Inet? inet)
    {
        if (inet?.Address is null) return "";
        return $"{FormatUInt32(inet.Address.Addr)}/{inet.NetworkLength}";
    }

    /// <summary>
    /// 把 Ipv4Inet 格式化为 a.b.c.d。
    /// </summary>
    private static string FormatIpv4(Ipv4Inet? inet)
    {
        if (inet?.Address is null) return "";
        return FormatUInt32(inet.Address.Addr);
    }

    /// <summary>
    /// protobuf 里的 IPv4 以网络字节序（大端）存放在 uint32 中。
    /// </summary>
    private static string FormatUInt32(uint address)
    {
        return $"{(address >> 24) & 0xFF}.{(address >> 16) & 0xFF}.{(address >> 8) & 0xFF}.{address & 0xFF}";
    }

    /// <summary>
    /// 探测端口是否接受本协议。
    /// </summary>
    public static bool TryConnect(string host, int port, int timeoutMs = 1500)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            if (!task.Wait(timeoutMs)) return false;
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static InstanceIdentifier BuildInstance(string? instanceName)
    {
        var identifier = new InstanceIdentifier();
        if (!string.IsNullOrWhiteSpace(instanceName))
        {
            identifier.InstanceSelector = new InstanceIdentifier.Types.InstanceSelector { Name = instanceName };
        }
        return identifier;
    }

    private byte[] Invoke(uint methodIndex, IMessage request)
    {
        using var client = new TcpClient();
        var connectTask = client.ConnectAsync(_host, _port);
        if (!connectTask.Wait(_timeoutMs))
        {
            throw new TimeoutException($"连接 EasyTier 管理端口 {_host}:{_port} 超时。");
        }

        client.ReceiveTimeout = _timeoutMs;
        using var stream = client.GetStream();

        var transactionId = System.Threading.Interlocked.Increment(ref _transactionId);
        var packet = new RpcPacket
        {
            FromPeer = 1,
            ToPeer = 1,
            TransactionId = transactionId,
            Descriptor_ = new RpcDescriptor
            {
                ProtoName = ServiceName,
                ServiceName = ServiceName,
                MethodIndex = methodIndex
            },
            Body = request.ToByteString(),
            IsRequest = true,
            TotalPieces = 1,
            PieceIdx = 0,
            CompressionInfo = new RpcCompressionInfo
            {
                // 声明只接受不压缩的响应，避免额外引入 zstd 依赖
                Algo = CompressionAlgoPb.None,
                AcceptedAlgo = CompressionAlgoPb.None
            }
        };

        WriteFrame(stream, packet);

        // 响应可能被切分为多个 RpcPacket（total_pieces > 1），必须按 piece_idx 合并后再解析
        var pieces = new SortedDictionary<uint, byte[]>();
        for (var attempt = 0; attempt < 256; attempt++)
        {
            var frame = ReadFrame(stream);
            if (frame is null)
            {
                throw new IOException("EasyTier 管理端口意外关闭了连接。");
            }
            if (frame.PacketType != PacketTypeRpcResp) continue;
            if (frame.TransactionId != transactionId) continue;

            if (frame.TotalPieces <= 1)
            {
                return ParseResponseBody(frame.Body, frame.Algo);
            }

            pieces[frame.PieceIdx] = frame.Body;
            if (pieces.Count < frame.TotalPieces) continue;

            using var merged = new MemoryStream();
            var complete = true;
            for (uint index = 0; index < frame.TotalPieces; index++)
            {
                if (!pieces.TryGetValue(index, out var part))
                {
                    complete = false;
                    break;
                }
                merged.Write(part, 0, part.Length);
            }
            if (complete) return ParseResponseBody(merged.ToArray(), frame.Algo);
            pieces.Clear();
        }

        throw new TimeoutException("等待 EasyTier 管理端口响应超时。");
    }

    /// <summary>
    /// 解析 RpcResponse：取出错误或返回真正的响应体。
    /// </summary>
    private static byte[] ParseResponseBody(byte[] body, CompressionAlgoPb compression)
    {
        if (compression == CompressionAlgoPb.Zstd)
        {
            throw new NotSupportedException("EasyTier 返回了 zstd 压缩响应，当前客户端未启用压缩支持。");
        }

        var response = RpcResponse.Parser.ParseFrom(body);
        if (response.Error is not null)
        {
            throw new EasyTierRpcException(response.Error.ErrorKindCase.ToString(), response.Error.ToString());
        }
        return response.Response.ToByteArray();
    }

    private static void WriteFrame(Stream stream, RpcPacket packet)
    {
        var body = packet.ToByteArray();
        var buffer = new byte[PeerManagerHeaderSize + body.Length + 4];
        var total = PeerManagerHeaderSize + body.Length;

        WriteUInt32(buffer, 0, (uint)total);
        WriteUInt32(buffer, 4, 1u);              // from_peer_id
        WriteUInt32(buffer, 8, 1u);              // to_peer_id
        buffer[12] = PacketTypeRpcReq;           // packet_type
        buffer[13] = 0;                          // flags
        buffer[14] = 1;                          // forward_counter
        buffer[15] = 0;                          // reserved
        WriteUInt32(buffer, 16, (uint)body.Length);

        Array.Copy(body, 0, buffer, 20, body.Length);
        stream.Write(buffer, 0, buffer.Length);
        stream.Flush();
    }

    /// <summary>
    /// 以小端写入 32 位无符号整数（.NET Framework 4.8 没有 BitConverter.TryWriteBytes）。
    /// </summary>
    private static void WriteUInt32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static Frame? ReadFrame(Stream stream)
    {
        var header = ReadExact(stream, 4);
        if (header is null) return null;
        var total = BitConverter.ToInt32(header, 0);
        if (total <= PeerManagerHeaderSize || total > MaxFrameBytes) return null;

        var rest = ReadExact(stream, total);
        if (rest is null) return null;

        var body = new byte[total - PeerManagerHeaderSize];
        Array.Copy(rest, PeerManagerHeaderSize, body, 0, body.Length);

        var packet = RpcPacket.Parser.ParseFrom(body);
        return new Frame
        {
            PacketType = rest[8],
            TransactionId = packet.TransactionId,
            Algo = packet.CompressionInfo?.Algo ?? CompressionAlgoPb.Invalid,
            TotalPieces = packet.TotalPieces,
            PieceIdx = packet.PieceIdx,
            Body = packet.Body.ToByteArray()
        };
    }

    private static byte[]? ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) return null;
            read += n;
        }
        return buffer;
    }

    public void Dispose()
    {
        // 每次调用都使用独立连接，无需释放常驻资源
    }

    private sealed class Frame
    {
        public int PacketType;
        public long TransactionId;
        public CompressionAlgoPb Algo;
        public uint TotalPieces;
        public uint PieceIdx;
        public byte[] Body = Array.Empty<byte>();
    }
}

/// <summary>
/// EasyTier 管理 RPC 返回的错误。
/// </summary>
public sealed class EasyTierRpcException : Exception
{
    public string ErrorKind { get; }

    public EasyTierRpcException(string errorKind, string message) : base($"EasyTier RPC 错误（{errorKind}）：{message}")
    {
        ErrorKind = errorKind;
    }
}
