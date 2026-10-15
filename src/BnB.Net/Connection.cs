using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace BnB.Net;

public readonly record struct Packet(MsgType Type, byte[] Body)
{
    public BinaryReader Reader() => new(new MemoryStream(Body), Encoding.UTF8);
}

// TCP 연결 하나. 수신은 전용 스레드가 받아서 큐에 넣고, 사용하는 쪽(게임 루프)이 꺼내 처리한다.
// 락스텝은 입력 순서가 바뀌면 안 되므로, 순서와 도착을 보장하는 TCP를 쓴다.
public sealed class Connection : IDisposable
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;
    private readonly object _sendLock = new();
    private readonly Thread _recvThread;
    private volatile bool _closed;

    public ConcurrentQueue<Packet> Incoming { get; } = new();
    public bool IsClosed => _closed;
    public string Remote { get; }

    // 서버에서 패킷마다 바로 처리하고 싶을 때 (입력은 큐를 거치지 않고 즉시 반영)
    public Action<Connection, Packet>? OnPacket { get; set; }
    public Action<Connection>? OnClosed { get; set; }

    public Connection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;  // 작은 패킷을 모아 보내는 Nagle 끔 → 입력 지연 감소
        _stream = client.GetStream();
        Remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        _recvThread = new Thread(ReceiveLoop) { IsBackground = true, Name = $"recv {Remote}" };
    }

    public void StartReceiving() => _recvThread.Start();

    public static Connection Connect(string host, int port)
    {
        var client = new TcpClient();
        client.Connect(host, port);
        var conn = new Connection(client);
        conn.StartReceiving();
        return conn;
    }

    public void Send(byte[] packet)
    {
        if (_closed)
            return;
        try
        {
            lock (_sendLock)
                _stream.Write(packet, 0, packet.Length);
        }
        catch (Exception)
        {
            Close();
        }
    }

    private void ReceiveLoop()
    {
        var header = new byte[2];
        try
        {
            while (!_closed)
            {
                _stream.ReadExactly(header);
                int len = header[0] | (header[1] << 8);
                if (len < 1)
                    throw new InvalidDataException("bad packet length");
                var data = new byte[len];
                _stream.ReadExactly(data);

                var packet = new Packet((MsgType)data[0], data[1..]);
                if (OnPacket is { } handler)
                    handler(this, packet);
                else
                    Incoming.Enqueue(packet);
            }
        }
        catch (Exception)
        {
            // 상대가 끊었거나 Close() 호출
        }
        Close();
    }

    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        try { _client.Close(); } catch { }
        OnClosed?.Invoke(this);
    }

    public void Dispose() => Close();
}
