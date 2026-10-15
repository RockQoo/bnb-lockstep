using BnB.Bot;
using BnB.Net;
using BnB.Sim;

// 사용법: BnB.Bot [--host 127.0.0.1] [--port 7777] [--name Bot] [--games N]
// 서버에 붙어서 실제 클라이언트와 같은 방식(Frame 수신 → Sim 진행 → 해시 보고)으로 동작한다.
string host = "127.0.0.1";
int port = Protocol.DefaultPort;
string name = "Bot";
int games = 1;
for (int i = 0; i < args.Length; ++i)
{
    switch (args[i])
    {
        case "--host": host = args[++i]; break;
        case "--port": port = int.Parse(args[++i]); break;
        case "--name": name = args[++i]; break;
        case "--games": games = int.Parse(args[++i]); break;
        default: throw new ArgumentException($"unknown option {args[i]}");
    }
}

void Log(string msg) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{name}] {msg}");

for (int game = 1; games == 0 || game <= games; ++game)
{
    Connection conn;
    try
    {
        conn = ConnectWithRetry(host, port);
    }
    catch (Exception e)
    {
        Log($"connect failed: {e.Message}");
        return 1;
    }

    conn.Send(Protocol.Hello(name));
    World? world = null;
    WelcomeMsg welcome = default;
    var brain = new BotBrain(Environment.TickCount ^ name.GetHashCode());
    int desyncs = 0;
    bool over = false;

    while (!over && !conn.IsClosed)
    {
        if (!conn.Incoming.TryDequeue(out Packet packet))
        {
            Thread.Sleep(1);
            continue;
        }

        switch (packet.Type)
        {
            case MsgType.Welcome:
                welcome = Protocol.ReadWelcome(packet.Reader());
                Log($"joined as P{welcome.PlayerIndex + 1}/{welcome.PlayerCount} seed={welcome.Seed}");
                break;
            case MsgType.Start:
                world = new World(MapData.Get(welcome.MapId), welcome.PlayerCount, welcome.Seed);
                break;
            case MsgType.Frame when world is not null:
            {
                FrameMsg frame = Protocol.ReadFrame(packet.Reader(), welcome.PlayerCount);
                if (frame.Tick != world.Tick)
                    Log($"frame order error: got {frame.Tick}, expected {world.Tick}");
                Replay.StepWorld(world, frame.Inputs);
                if (frame.Tick % Protocol.HashInterval == 0)
                    conn.Send(Protocol.Hash(new HashMsg(frame.Tick, world.ComputeHash())));

                conn.Send(Protocol.Input(brain.Decide(world, welcome.PlayerIndex)));
                break;
            }
            case MsgType.Desync:
                ++desyncs;
                Log($"DESYNC reported at tick {Protocol.ReadDesync(packet.Reader()).Tick}");
                break;
            case MsgType.GameOver:
            {
                GameOverMsg m = Protocol.ReadGameOver(packet.Reader());
                ulong mine = world?.ComputeHash() ?? 0;
                string result = (ResultKind)m.Result == ResultKind.Win ? $"P{m.Winner + 1} wins" : "draw";
                Log($"game over: {result} at tick {m.Tick}, server={m.FinalHash:X16} local={mine:X16} "
                    + $"{(mine == m.FinalHash ? "MATCH" : "MISMATCH")}, desyncs={desyncs}");
                over = true;
                break;
            }
        }
    }

    conn.Close();
    Thread.Sleep(500);
}
return 0;

static Connection ConnectWithRetry(string host, int port)
{
    for (int attempt = 0; ; ++attempt)
    {
        try
        {
            return Connection.Connect(host, port);
        }
        catch when (attempt < 50)
        {
            Thread.Sleep(200);
        }
    }
}
