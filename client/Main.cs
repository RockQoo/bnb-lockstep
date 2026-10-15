using System.Collections.Generic;
using System.Threading.Tasks;
using BnB.Client;
using BnB.Net;
using BnB.Sim;
using Godot;

// 클라이언트 흐름
//   접속 → 로비 → 라이브(서버 Frame을 받을 때마다 Sim 한 틱) → 게임 종료 → 방금 판 리플레이 자동 재생 → 결과
// 클라는 상태를 서버에서 받지 않는다. 시드와 입력(Frame)만 받아서 같은 Sim을 돌린다.
public partial class Main : Node2D
{
    private enum Phase { Connecting, Lobby, Live, Replay, Finished }

    private readonly BoardRenderer _renderer = new();
    private Font _font = null!;

    // 실행 인자
    private string _host = "127.0.0.1";
    private int _port = Protocol.DefaultPort;
    private string _name = "Player";
    private string? _replayFile;
    private string? _screenshotPath;
    private double _screenshotDelay = 3;
    private BnB.Bot.BotBrain? _autoplay;
    private bool _quitAfterReplay;

    private Phase _phase = Phase.Connecting;
    private string _status = "";
    private Connection? _conn;
    private Task<Connection>? _connectTask;
    private WelcomeMsg _welcome;
    private GameOverMsg? _gameOver;
    private int _desyncs;
    private int _lobbyConnected;

    // 화면에 그리는 월드 (라이브 또는 리플레이)
    private World? _world;
    private Vector2[] _prevPos = [];
    private Vector2[] _curPos = [];
    private double _lastStepTime;

    // 라이브 중 받은 Frame 기록 → 판이 끝나면 그대로 리플레이 재생
    private readonly List<byte[]> _recorded = [];

    // 리플레이 재생
    private Replay? _replay;
    private int _replayIndex;
    private double _replayAcc;
    private int _replaySpeed = 1;
    private bool _replayFromFile;
    private string _replayCheck = "";

    // 입력
    private readonly List<Key> _heldDirs = [];
    private byte _pendingButtons;
    private byte _lastSentDir = 255;

    private double _time;
    private const double TickSec = 1.0 / Rules.TickRate;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        ParseArgs(OS.GetCmdlineUserArgs());
        DisplayServer.WindowSetTitle($"BnB Lockstep - {_name}");

        if (_replayFile is not null)
        {
            try
            {
                StartReplay(Replay.Load(_replayFile), fromFile: true);
            }
            catch (System.Exception e)
            {
                _status = $"replay load failed: {e.Message}";
                _phase = Phase.Finished;
            }
        }
        else
        {
            BeginConnect();
        }

        if (_screenshotPath is not null && !_quitAfterReplay)
            TakeScreenshotLater();
    }

    private void ParseArgs(string[] args)
    {
        for (int i = 0; i < args.Length; ++i)
        {
            string Next() => i + 1 < args.Length ? args[++i] : "";
            switch (args[i])
            {
                case "--host": _host = Next(); break;
                case "--port": _port = int.Parse(Next()); break;
                case "--name": _name = Next(); break;
                case "--replay": _replayFile = Next(); break;
                case "--screenshot": _screenshotPath = Next(); break;
                case "--shot-delay": _screenshotDelay = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
                case "--autoplay": _autoplay = new BnB.Bot.BotBrain(System.Environment.TickCount); break;
                case "--quit-after-replay": _quitAfterReplay = true; break;
                case "--replay-speed": _replaySpeed = int.Parse(Next()); break;
            }
        }
    }

    private async void TakeScreenshotLater()
    {
        await ToSignal(GetTree().CreateTimer(_screenshotDelay), SceneTreeTimer.SignalName.Timeout);
        CaptureAndQuit();
    }

    // 테스트용: 화면을 PNG로 저장하고 종료
    private async void CaptureAndQuit()
    {
        if (_screenshotPath is not null)
        {
            QueueRedraw();
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
            GetViewport().GetTexture().GetImage().SavePng(_screenshotPath);
        }
        GetTree().Quit();
    }

    // ------------------------------------------------------------------ 접속

    private void BeginConnect()
    {
        _phase = Phase.Connecting;
        _status = $"Connecting to {_host}:{_port} ...";
        _gameOver = null;
        _desyncs = 0;
        _world = null;
        _recorded.Clear();
        string host = _host;
        int port = _port;
        _connectTask = Task.Run(() => Connection.Connect(host, port));
    }

    private void PollConnect()
    {
        if (_connectTask is null || !_connectTask.IsCompleted)
            return;

        if (_connectTask.IsFaulted)
        {
            _status = $"Connect failed ({_host}:{_port}). Retrying...";
            _connectTask = Task.Delay(1000).ContinueWith(_ => Connection.Connect(_host, _port));
            return;
        }

        _conn = _connectTask.Result;
        _connectTask = null;
        _conn.Send(Protocol.Hello(_name));
        _phase = Phase.Lobby;
        _status = "Waiting for players...";
    }

    private void PollNetwork()
    {
        if (_conn is null)
            return;

        while (_conn.Incoming.TryDequeue(out Packet packet))
        {
            switch (packet.Type)
            {
                case MsgType.Welcome:
                    _welcome = Protocol.ReadWelcome(packet.Reader());
                    break;
                case MsgType.Lobby:
                {
                    LobbyMsg m = Protocol.ReadLobby(packet.Reader());
                    _lobbyConnected = m.Connected;
                    _status = $"Waiting for players... {m.Connected}/{m.Needed}";
                    break;
                }
                case MsgType.Start:
                    StartLive();
                    break;
                case MsgType.Frame when _world is not null && _phase == Phase.Live:
                {
                    FrameMsg frame = Protocol.ReadFrame(packet.Reader(), _welcome.PlayerCount);
                    _recorded.Add(frame.Inputs);
                    StepWorld(frame.Inputs);
                    if (frame.Tick % Protocol.HashInterval == 0)
                        _conn.Send(Protocol.Hash(new HashMsg(frame.Tick, _world.ComputeHash())));
                    if (_autoplay is not null)
                        _conn.Send(Protocol.Input(_autoplay.Decide(_world, _welcome.PlayerIndex)));
                    break;
                }
                case MsgType.Desync:
                    ++_desyncs;
                    break;
                case MsgType.GameOver:
                    _gameOver = Protocol.ReadGameOver(packet.Reader());
                    OnGameOver();
                    return;
            }
        }

        if (_conn.IsClosed && _phase is Phase.Lobby or Phase.Live)
        {
            _status = "Disconnected. Press Enter to reconnect.";
            _phase = Phase.Finished;
        }
    }

    private void StartLive()
    {
        _world = new World(MapData.Get(_welcome.MapId), _welcome.PlayerCount, _welcome.Seed);
        _recorded.Clear();
        ResetInterpolation();
        _phase = Phase.Live;
        _status = "";
    }

    private void OnGameOver()
    {
        _conn?.Close();
        _conn = null;

        // 방금 받은 입력 기록으로 리플레이를 만든다. 서버가 저장한 리플레이와 같은 내용이다.
        var replay = new Replay { MapId = _welcome.MapId, PlayerCount = _welcome.PlayerCount, Seed = _welcome.Seed };
        replay.Frames.AddRange(_recorded);
        replay.FinalHash = _gameOver!.Value.FinalHash;
        SaveLocalReplay(replay);
        StartReplay(replay, fromFile: false);
    }

    private void SaveLocalReplay(Replay replay)
    {
        try
        {
            string dir = ProjectSettings.GlobalizePath("user://replays");
            System.IO.Directory.CreateDirectory(dir);
            replay.Save(System.IO.Path.Combine(dir, $"{System.DateTime.Now:yyyyMMdd-HHmmss}-seed{replay.Seed}.bnbreplay"));
        }
        catch (System.Exception e)
        {
            GD.PrintErr($"replay save failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ 진행

    private void ResetInterpolation()
    {
        int n = _world!.Players.Length;
        _curPos = new Vector2[n];
        _prevPos = new Vector2[n];
        for (int i = 0; i < n; ++i)
            _curPos[i] = _prevPos[i] = new Vector2(_world.Players[i].X, _world.Players[i].Y);
        _lastStepTime = _time;
    }

    private void StepWorld(byte[] frame)
    {
        Replay.StepWorld(_world!, frame);
        for (int i = 0; i < _curPos.Length; ++i)
        {
            _prevPos[i] = _curPos[i];
            _curPos[i] = new Vector2(_world!.Players[i].X, _world.Players[i].Y);
        }
        _lastStepTime = _time;
    }

    private void StartReplay(Replay replay, bool fromFile)
    {
        _replay = replay;
        _replayFromFile = fromFile;
        _replayIndex = 0;
        _replayAcc = 0;
        _replayCheck = "";
        _world = replay.CreateWorld();
        ResetInterpolation();
        _phase = Phase.Replay;
    }

    private void AdvanceReplay(double delta)
    {
        _replayAcc += delta * _replaySpeed;
        while (_replayAcc >= TickSec && _replayIndex < _replay!.Frames.Count)
        {
            _replayAcc -= TickSec;
            StepWorld(_replay.Frames[_replayIndex++]);
        }

        if (_replayIndex >= _replay!.Frames.Count)
        {
            // 입력만으로 다시 돌린 결과가 원래 판의 마지막 해시와 같은지 확인 (결정론 검증)
            ulong hash = _world!.ComputeHash();
            _replayCheck = hash == _replay.FinalHash
                ? $"Replay hash MATCH {hash:X16}"
                : $"Replay hash MISMATCH local={hash:X16} recorded={_replay.FinalHash:X16}";
            GD.Print(_replayCheck);
            _phase = Phase.Finished;
            if (_quitAfterReplay)
                CaptureAndQuit();
        }
    }

    public override void _Process(double delta)
    {
        _time += delta;

        switch (_phase)
        {
            case Phase.Connecting:
                PollConnect();
                break;
            case Phase.Lobby:
            case Phase.Live:
                PollNetwork();
                if (_phase == Phase.Live && _autoplay is null)
                    SendInput();
                break;
            case Phase.Replay:
                AdvanceReplay(delta);
                break;
        }

        QueueRedraw();
    }

    // ------------------------------------------------------------------ 입력

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key)
            return;

        Key k = key.PhysicalKeycode;
        bool isDir = k is Key.Up or Key.Down or Key.Left or Key.Right or Key.W or Key.A or Key.S or Key.D;

        if (key.Pressed && !key.Echo)
        {
            if (isDir)
            {
                _heldDirs.Remove(k);
                _heldDirs.Add(k);  // 가장 최근에 누른 방향이 우선
            }
            switch (k)
            {
                case Key.Space: _pendingButtons |= PlayerInput.BombBit; break;
                case Key.Q or Key.Ctrl: _pendingButtons |= PlayerInput.NeedleBit; break;
                case Key.E or Key.Shift: _pendingButtons |= PlayerInput.DartBit; break;
            }
            HandleMenuKey(k);
        }
        else if (!key.Pressed && isDir)
        {
            _heldDirs.Remove(k);
        }
    }

    private void HandleMenuKey(Key k)
    {
        if (_phase is Phase.Replay or Phase.Finished && _replay is not null)
        {
            switch (k)
            {
                case Key.R: StartReplay(_replay, _replayFromFile); return;
                case Key.Key1: _replaySpeed = 1; return;
                case Key.Key2: _replaySpeed = 2; return;
                case Key.Key3: _replaySpeed = 4; return;
            }
        }
        if (_phase == Phase.Finished && k == Key.Enter && !_replayFromFile)
            BeginConnect();
        if (k == Key.Escape)
            GetTree().Quit();
    }

    private Dir CurrentDir()
    {
        if (_heldDirs.Count == 0)
            return Dir.None;
        return _heldDirs[^1] switch
        {
            Key.Up or Key.W => Dir.Up,
            Key.Down or Key.S => Dir.Down,
            Key.Left or Key.A => Dir.Left,
            Key.Right or Key.D => Dir.Right,
            _ => Dir.None,
        };
    }

    private void SendInput()
    {
        if (_conn is null)
            return;
        byte dir = (byte)CurrentDir();
        if (dir == _lastSentDir && _pendingButtons == 0)
            return;
        _conn.Send(Protocol.Input((byte)(dir | _pendingButtons)));
        _lastSentDir = dir;
        _pendingButtons = 0;
    }

    // ------------------------------------------------------------------ 그리기

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, GetViewportRect().Size), new Color(0.12f, 0.13f, 0.16f));

        string title = _phase switch
        {
            Phase.Replay => $"REPLAY  x{_replaySpeed}",
            Phase.Live => "LIVE",
            Phase.Finished when _replay is not null => "REPLAY END",
            _ => "BnB Lockstep",
        };
        DrawString(_font, new Vector2(16, 28), title, HorizontalAlignment.Left, -1, 20,
                   _phase == Phase.Replay ? new Color(1f, 0.85f, 0.3f) : Colors.White);

        if (_world is not null)
        {
            double alpha = System.Math.Clamp((_time - _lastStepTime) / (TickSec / _replaySpeedForInterp()), 0, 1);
            var pos = new Vector2[_curPos.Length];
            for (int i = 0; i < pos.Length; ++i)
                pos[i] = _prevPos[i].Lerp(_curPos[i], (float)alpha);
            _renderer.Draw(this, _world, pos, _phase == Phase.Live || !_replayFromFile ? _welcome.PlayerIndex : -1, _time);
        }

        DrawHud();
    }

    private double _replaySpeedForInterp() => _phase == Phase.Replay ? _replaySpeed : 1;

    private void DrawHud()
    {
        float x = 700;
        float y = 60;
        Color dim = new(0.7f, 0.72f, 0.78f);

        void Line(string text, Color? color = null, int size = 14)
        {
            DrawString(_font, new Vector2(x, y), text, HorizontalAlignment.Left, 290, size, color ?? Colors.White);
            y += size + 8;
        }

        if (_status.Length > 0)
            Line(_status, new Color(1f, 0.85f, 0.4f));

        if (_world is not null)
        {
            int secLeft = System.Math.Max(0, (Rules.TimeLimitTicks - _world.Tick) / Rules.TickRate);
            Line($"Tick {_world.Tick}   Time {secLeft / 60}:{secLeft % 60:00}", dim);
            Line($"Hash {_world.ComputeHash():X16}", dim, 12);
            if (_phase == Phase.Live || _gameOver is not null)
                Line($"Seed {_welcome.Seed}   Desync {_desyncs}", _desyncs > 0 ? new Color(1, 0.3f, 0.3f) : dim, 12);
            y += 6;

            foreach (Player p in _world.Players)
            {
                Color c = BoardRenderer.PlayerColors[p.Index % BoardRenderer.PlayerColors.Length];
                DrawRect(new Rect2(x, y - 12, 14, 14), c);
                string me = p.Index == _welcome.PlayerIndex && !_replayFromFile ? " (me)" : "";
                Line($"    P{p.Index + 1}{me}  {p.State}", p.State == PlayerState.Dead ? dim : Colors.White);
                Line($"    Bomb {p.BombCapacity}  Range {p.Range}  Skate {p.Skate}", dim, 12);
                Line($"    Needle {p.Needles}  Dart {p.Darts}", dim, 12);
            }
        }

        if (_gameOver is not null || (_phase == Phase.Finished && _world is not null))
        {
            y += 6;
            // 리플레이 재생 중에는 결과를 미리 보여주지 않는다
            string result = "";
            if (_phase == Phase.Finished)
            {
                // 서버 판정(기권승 포함)이 있으면 그걸, 파일 리플레이면 Sim 결과를 쓴다
                (ResultKind kind, int winner) = _gameOver is { } go
                    ? ((ResultKind)go.Result, go.Winner)
                    : (_world!.Result, _world.Winner);
                bool forfeit = _gameOver is not null && _world!.Result == ResultKind.Ongoing;
                result = kind switch
                {
                    ResultKind.Win => $"P{winner + 1} WINS!{(forfeit ? " (forfeit)" : "")}",
                    ResultKind.Draw => "DRAW",
                    _ => "",
                };
            }
            if (result.Length > 0)
                Line(result, new Color(1f, 0.9f, 0.3f), 22);
        }

        if (_replayCheck.Length > 0)
            Line(_replayCheck, _replayCheck.Contains("MISMATCH") ? new Color(1, 0.3f, 0.3f) : new Color(0.4f, 1f, 0.5f), 12);

        y = 520;
        if (_phase is Phase.Replay or Phase.Finished)
        {
            Line("R: replay again   1/2/3: speed x1/x2/x4", dim, 12);
            if (!_replayFromFile)
                Line("Enter: next game   Esc: quit", dim, 12);
            else
                Line("Esc: quit", dim, 12);
        }
        else
        {
            Line("Move: Arrows / WASD", dim, 12);
            Line("Space: water balloon", dim, 12);
            Line("Q / Ctrl: needle   E / Shift: dart", dim, 12);
        }
    }
}
