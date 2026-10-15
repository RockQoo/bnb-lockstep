using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using BnB.Net;
using BnB.Sim;

namespace BnB.Server;

// 서버 역할
//   1) 시계: 30Hz로 틱을 진행한다. 느린 클라를 기다리지 않는다(그 틱에 입력이 없으면 직전 방향을 유지).
//   2) 릴레이: 틱마다 모든 플레이어의 입력을 모아 Frame으로 뿌린다. 클라는 Frame을 받아야만 진행한다.
//   3) 검증: 서버도 같은 Sim을 돌리고, 클라가 보낸 해시와 비교해 디싱크를 잡는다.
//   4) 기록: 판이 끝나면 시드 + 입력만으로 리플레이 파일을 저장한다.
public sealed class GameServer
{
    private readonly ServerOptions _options;
    private readonly TcpListener _listener;

    public GameServer(ServerOptions options)
    {
        _options = options;
        _listener = new TcpListener(IPAddress.Any, options.Port);
    }

    private static void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {msg}");

    public void Run()
    {
        _listener.Start();
        Log($"listening on :{_options.Port}, players={_options.Players}, map={_options.MapId}");
        Directory.CreateDirectory(_options.ReplayDir);

        for (int game = 1; _options.Games == 0 || game <= _options.Games; ++game)
        {
            uint seed = _options.Seed ?? (uint)Random.Shared.NextInt64(1, uint.MaxValue);
            var match = new Match(_options, seed, Log);
            match.WaitForPlayers(_listener);
            match.Play();
        }

        _listener.Stop();
        Log("server stopped");
    }
}

internal sealed class Match
{
    private readonly ServerOptions _options;
    private readonly uint _seed;
    private readonly Action<string> _log;
    private readonly Connection?[] _players;
    private readonly string[] _names;

    // 입력은 수신 스레드에서 들어오고 게임 루프에서 꺼낸다
    private readonly object _inputLock = new();
    private readonly byte[] _latestDir;
    private readonly byte[] _pendingButtons;

    private readonly ConcurrentDictionary<(int Player, int Tick), ulong> _clientHashes = new();
    private readonly ulong[] _serverHashes = new ulong[Rules.TimeLimitTicks + 2];
    private int _desyncCount;
    private volatile int _serverTick = -1;

    public Match(ServerOptions options, uint seed, Action<string> log)
    {
        _options = options;
        _seed = seed;
        _log = log;
        _players = new Connection?[options.Players];
        _names = new string[options.Players];
        _latestDir = new byte[options.Players];
        _pendingButtons = new byte[options.Players];
    }

    private int ConnectedCount => _players.Count(p => p is { IsClosed: false });

    private void Broadcast(byte[] packet)
    {
        foreach (Connection? c in _players)
            c?.Send(packet);
    }

    public void WaitForPlayers(TcpListener listener)
    {
        _log($"lobby open (seed={_seed})");
        while (ConnectedCount < _players.Length)
        {
            TcpClient tcp = listener.AcceptTcpClient();
            var conn = new Connection(tcp);
            conn.StartReceiving();

            // 첫 패킷은 Hello여야 한다
            Packet hello = WaitPacket(conn, TimeSpan.FromSeconds(5));
            if (hello.Type != MsgType.Hello)
            {
                conn.Close();
                continue;
            }

            int slot = Array.FindIndex(_players, p => p is null || p.IsClosed);
            _players[slot] = conn;
            _names[slot] = Protocol.ReadHello(hello.Reader());
            _log($"P{slot + 1} '{_names[slot]}' joined from {conn.Remote}");

            int index = slot;
            conn.OnPacket = (c, p) => OnPacket(index, p);
            conn.OnClosed = _ => _log($"P{index + 1} disconnected");
            // Hello 이후 큐에 쌓인 패킷이 있으면 처리
            while (conn.Incoming.TryDequeue(out Packet queued))
                OnPacket(index, queued);

            conn.Send(Protocol.Welcome(new WelcomeMsg(slot, _players.Length, _seed, _options.MapId)));
            Broadcast(Protocol.Lobby(new LobbyMsg(ConnectedCount, _players.Length)));
        }
    }

    private static Packet WaitPacket(Connection conn, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !conn.IsClosed)
        {
            if (conn.Incoming.TryDequeue(out Packet p))
                return p;
            Thread.Sleep(5);
        }
        return new Packet(0, []);
    }

    // 수신 스레드에서 호출된다
    private void OnPacket(int player, Packet packet)
    {
        switch (packet.Type)
        {
            case MsgType.Input:
            {
                byte raw = Protocol.ReadInput(packet.Reader());
                lock (_inputLock)
                {
                    _latestDir[player] = (byte)(raw & 0x7);
                    // 버튼은 다음 틱까지 모아서(OR) 한 번 반영 → 틱 사이에 짧게 눌러도 사라지지 않는다
                    _pendingButtons[player] |= (byte)(raw & PlayerInput.ButtonMask);
                }
                break;
            }
            case MsgType.Hash:
            {
                HashMsg h = Protocol.ReadHash(packet.Reader());
                _clientHashes[(player, h.Tick)] = h.Hash;
                if (h.Tick <= _serverTick)
                    CompareHash(player, h.Tick);
                break;
            }
        }
    }

    private void CompareHash(int player, int tick)
    {
        if (!_clientHashes.TryRemove((player, tick), out ulong clientHash))
            return;
        ulong serverHash = _serverHashes[tick];
        if (clientHash != serverHash)
        {
            Interlocked.Increment(ref _desyncCount);
            _log($"DESYNC P{player + 1} tick={tick} client={clientHash:X16} server={serverHash:X16}");
            _players[player]?.Send(Protocol.Desync(new DesyncMsg(tick, player)));
        }
    }

    public void Play()
    {
        var world = new World(MapData.Get(_options.MapId), _players.Length, _seed);
        var replay = new Replay { MapId = _options.MapId, PlayerCount = _players.Length, Seed = _seed };

        Broadcast(Protocol.Start());
        _log($"game start: {string.Join(", ", _names.Select((n, i) => $"P{i + 1}={n}"))}");

        var inputs = new PlayerInput[_players.Length];
        long tickTicks = Stopwatch.Frequency / Rules.TickRate;
        var clock = Stopwatch.StartNew();
        long nextTick = 0;

        ResultKind result = ResultKind.Ongoing;
        int winner = -1;
        bool forfeit = false;

        while (world.Result == ResultKind.Ongoing)
        {
            if (ConnectedCount == 0)
            {
                _log("all players left, aborting game");
                return;
            }
            if (_players.Length > 1 && ConnectedCount == 1)
            {
                // 혼자 남으면 기권승. Sim 밖의 판정이라 결과는 GameOver 메시지로만 전달한다.
                forfeit = true;
                result = ResultKind.Win;
                winner = Array.FindIndex(_players, p => p is { IsClosed: false });
                break;
            }

            // 고정 틱: 다음 틱 시각까지 대기
            long now = clock.ElapsedTicks;
            if (now < nextTick)
            {
                long remainMs = (nextTick - now) * 1000 / Stopwatch.Frequency;
                if (remainMs > 1)
                    Thread.Sleep((int)remainMs - 1);
                else
                    Thread.SpinWait(50);
                continue;
            }
            nextTick += tickTicks;

            // 이번 틱 입력 확정
            var frame = new byte[_players.Length];
            lock (_inputLock)
            {
                for (int i = 0; i < frame.Length; ++i)
                {
                    bool connected = _players[i] is { IsClosed: false };
                    frame[i] = connected ? (byte)(_latestDir[i] | _pendingButtons[i]) : (byte)0;
                    _pendingButtons[i] = 0;
                }
            }

            int tick = world.Tick;
            Broadcast(Protocol.Frame(new FrameMsg(tick, frame)));
            replay.Frames.Add(frame);

            for (int i = 0; i < frame.Length; ++i)
                inputs[i] = new PlayerInput(frame[i]);
            world.Step(inputs);

            _serverHashes[tick] = world.ComputeHash();
            _serverTick = tick;
            for (int p = 0; p < _players.Length; ++p)
                if (_clientHashes.ContainsKey((p, tick)))
                    CompareHash(p, tick);

            if (tick % (Rules.TickRate * 10) == 0)
                _log($"tick {tick} hash={_serverHashes[tick]:X16}");
        }

        if (!forfeit)
        {
            result = world.Result;
            winner = world.Winner;
        }

        ulong finalHash = world.ComputeHash();
        replay.FinalHash = finalHash;
        string resultText = result == ResultKind.Win ? $"P{winner + 1} wins{(forfeit ? " (forfeit)" : "")}" : "draw";
        _log($"game over at tick {world.Tick}: {resultText}, finalHash={finalHash:X16}, desyncs={_desyncCount}");
        Broadcast(Protocol.GameOver(new GameOverMsg((byte)result, winner, world.Tick, finalHash)));

        string path = Path.Combine(_options.ReplayDir, $"{DateTime.Now:yyyyMMdd-HHmmss}-seed{_seed}.bnbreplay");
        replay.Save(path);
        bool ok = Replay.Load(path).Verify(out ulong replayHash);
        _log($"replay saved: {path} ({replay.Frames.Count} ticks), verify={(ok ? "OK" : $"FAIL {replayHash:X16}")}");

        // 마지막 해시 보고를 받을 시간을 잠깐 준 뒤 연결 종료
        Thread.Sleep(1500);
        foreach (Connection? c in _players)
            c?.Close();
    }
}
