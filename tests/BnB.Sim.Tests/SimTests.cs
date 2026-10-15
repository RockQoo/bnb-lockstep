using BnB.Sim;

namespace BnB.Sim.Tests;

public class SimTests
{
    private const int C = Rules.CellSize;
    private const int H = Rules.Half;

    private static World MakeWorld(int players, params string[] rows)
        => new(MapData.Parse("test", rows), players, 1234);

    private static void Step(World w, params PlayerInput[] inputs) => w.Step(inputs);

    private static void Idle(World w, int ticks)
    {
        var idle = new PlayerInput[w.Players.Length];
        for (int i = 0; i < ticks; ++i)
            w.Step(idle);
    }

    private static PlayerInput In(Dir d, bool bomb = false, bool needle = false, bool dart = false)
        => PlayerInput.Make(d, bomb, needle, dart);

    private static PlayerInput None => default;

    private static void AddBomb(World w, int x, int y, int range, int fuse = 1)
        => w.Bombs.Add(new Bomb { Id = 1000 + w.Bombs.Count, Owner = 0, CellX = x, CellY = y, Range = range, Fuse = fuse });

    // ------------------------------------------------------------ 결정론

    [Fact]
    public void SameSeedAndInputs_ProduceSameHashEveryTick()
    {
        var a = new World(MapData.Pirate14, 4, 42);
        var b = new World(MapData.Pirate14, 4, 42);
        var rng = new Rng(7);
        var inputs = new PlayerInput[4];

        for (int tick = 0; tick < 3000; ++tick)
        {
            for (int i = 0; i < 4; ++i)
                inputs[i] = new PlayerInput((byte)(rng.Next(5) | (rng.Next(6) == 0 ? PlayerInput.BombBit : 0)
                                                               | (rng.Next(20) == 0 ? PlayerInput.DartBit : 0)
                                                               | (rng.Next(20) == 0 ? PlayerInput.NeedleBit : 0)));
            a.Step(inputs);
            b.Step(inputs);
            Assert.Equal(a.ComputeHash(), b.ComputeHash());
        }
    }

    [Fact]
    public void DifferentSeed_ChangesItemDrops()
    {
        static ulong Run(uint seed)
        {
            var w = new World(MapData.Pirate14, 2, seed);
            // P1이 시작 위치에서 물풍선을 놓고 위로 피한다 → 오른쪽 상자가 부서지며 아이템이 시드에 따라 나온다
            var rng = new Rng(99);
            var inputs = new PlayerInput[2];
            for (int t = 0; t < 600; ++t)
            {
                inputs[0] = new PlayerInput((byte)(rng.Next(5) | (rng.Next(8) == 0 ? PlayerInput.BombBit : 0)));
                inputs[1] = new PlayerInput((byte)(rng.Next(5) | (rng.Next(8) == 0 ? PlayerInput.BombBit : 0)));
                w.Step(inputs);
            }
            return w.ComputeHash();
        }

        Assert.NotEqual(Run(1), Run(2));
        Assert.Equal(Run(1), Run(1));
    }

    [Fact]
    public void Replay_RoundTrip_VerifiesFinalHash()
    {
        var replay = new Replay { MapId = "pirate14", PlayerCount = 2, Seed = 77 };
        World w = replay.CreateWorld();
        var rng = new Rng(5);
        for (int t = 0; t < 900; ++t)
        {
            byte[] frame = [(byte)(rng.Next(5) | (rng.Next(7) == 0 ? PlayerInput.BombBit : 0)), (byte)rng.Next(5)];
            replay.Frames.Add(frame);
            Replay.StepWorld(w, frame);
        }
        replay.FinalHash = w.ComputeHash();

        var ms = new MemoryStream();
        replay.Write(ms);
        ms.Position = 0;
        Replay loaded = Replay.Read(ms);

        Assert.True(loaded.Verify(out _));

        // 입력이 달라지면 결과가 달라진다 (P1이 물풍선을 하나도 안 놓음)
        foreach (byte[] frame in loaded.Frames)
            frame[0] &= unchecked((byte)~PlayerInput.BombBit);
        Assert.False(loaded.Verify(out _));
    }

    // ------------------------------------------------------------ 폭발

    [Fact]
    public void Explosion_DestroysBlock_StopsAtHard()
    {
        World w = MakeWorld(1,
            "1......",
            ".......",
            "..BH...",
            "...B...");
        AddBomb(w, 2, 3, range: 3);  // (2,3) 위로 (2,2) 블록, 오른쪽 (3,3) 블록
        Idle(w, 1);

        Assert.Equal(Tile.Empty, w.TileAt(2, 2));
        Assert.Equal(Tile.Empty, w.TileAt(3, 3));
        Assert.Equal(Tile.Hard, w.TileAt(3, 2));
        Assert.True(w.Flames[w.Idx(2, 2)] > 0);
        Assert.Equal(0, w.Flames[w.Idx(2, 1)]);  // 블록에서 멈춤
    }

    [Fact]
    public void Explosion_ChainsToOtherBomb()
    {
        World w = MakeWorld(1,
            "1......",
            ".......");
        AddBomb(w, 2, 1, range: 2, fuse: 1);
        AddBomb(w, 4, 1, range: 2, fuse: 50);
        Idle(w, 1);

        Assert.Empty(w.Bombs);
        Assert.True(w.Flames[w.Idx(6, 1)] > 0);  // 두 번째 물풍선의 물줄기
    }

    [Fact]
    public void Flame_TrapsPlayer_ThenKills()
    {
        World w = MakeWorld(1, "1....");
        AddBomb(w, 2, 0, range: 2);
        Idle(w, 1);
        Assert.Equal(PlayerState.Trapped, w.Players[0].State);

        Idle(w, Rules.TrapTicks);
        Assert.Equal(PlayerState.Dead, w.Players[0].State);
    }

    [Fact]
    public void Needle_EscapesTrap()
    {
        World w = MakeWorld(1, "1....");
        w.Players[0].Needles = 1;
        AddBomb(w, 2, 0, range: 2);
        Idle(w, 1);
        Assert.Equal(PlayerState.Trapped, w.Players[0].State);

        Step(w, In(Dir.None, needle: true));
        Assert.Equal(PlayerState.Alive, w.Players[0].State);
        Assert.Equal(0, w.Players[0].Needles);
    }

    [Fact]
    public void Straddle_CenterOutsideFlame_IsSafe()
    {
        // 걸치기: 몸 절반이 물줄기 칸에 들어가도 중점 칸이 안전하면 산다
        World w = MakeWorld(1, "1......");
        AddBomb(w, 4, 0, range: 2);  // 물줄기 x = 2..6
        w.Players[0].X = 1 * C + H + (H - 1);  // 중점은 1번 칸 끝, 몸은 2번 칸에 걸침
        Idle(w, 1);
        Assert.Equal(PlayerState.Alive, w.Players[0].State);

        World w2 = MakeWorld(1, "1......");
        AddBomb(w2, 4, 0, range: 2);
        w2.Players[0].X = 2 * C;  // 중점이 2번 칸으로 넘어감
        Idle(w2, 1);
        Assert.Equal(PlayerState.Trapped, w2.Players[0].State);
    }

    [Fact]
    public void TrappedEnemy_PoppedByTouch()
    {
        World w = MakeWorld(2, "1.2..");
        w.Players[1].State = PlayerState.Trapped;
        w.Players[1].TrappedTicks = 100;
        for (int i = 0; i < 30; ++i)
            Step(w, In(Dir.Right), None);
        Assert.Equal(PlayerState.Dead, w.Players[1].State);
        Assert.Equal(ResultKind.Win, w.Result);
        Assert.Equal(0, w.Winner);
    }

    // ------------------------------------------------------------ 이동

    [Fact]
    public void Move_StopsAtCellCenterBeforeBlock()
    {
        World w = MakeWorld(1, "1.B..");
        for (int i = 0; i < 60; ++i)
            Step(w, In(Dir.Right));
        Assert.Equal(1 * C + H, w.Players[0].X);
    }

    [Fact]
    public void Move_CornerAssist_SlidesIntoLane()
    {
        // 위로 가고 싶은데 중점이 왼쪽 칸 오른쪽 끝에 있음 → 오른쪽 레인(골목)으로 밀려 들어감
        World w = MakeWorld(1,
            "B.B",
            "1..");
        w.Players[0].X = 0 * C + H + 300;  // 0번 칸, 오른쪽으로 300 치우침 (옆 레인 중심까지 700)
        for (int i = 0; i < 40; ++i)
            Step(w, In(Dir.Up));
        Assert.Equal(1 * C + H, w.Players[0].X);
        Assert.Equal(0, w.Players[0].CellY);
    }

    [Fact]
    public void Move_NoAssist_WhenTooFarFromLane()
    {
        World w = MakeWorld(1,
            "B.B",
            "1..");
        w.Players[0].X = 0 * C + H + 100;  // 옆 레인 기준에 못 미침
        for (int i = 0; i < 40; ++i)
            Step(w, In(Dir.Up));
        Assert.Equal(1, w.Players[0].CellY);
    }

    [Fact]
    public void Bomb_PassThroughUntilLeaving()
    {
        World w = MakeWorld(1, "...1...");
        Step(w, In(Dir.None, bomb: true));
        Assert.Single(w.Bombs);

        for (int i = 0; i < 30; ++i)
            Step(w, In(Dir.Right));  // 내 물풍선 위에서 벗어남
        int x = w.Players[0].X;
        Assert.True(x >= 5 * C);

        for (int i = 0; i < 30; ++i)
            Step(w, In(Dir.Left));   // 다시 돌아가려 하면 막힘
        Assert.Equal(4 * C + H, w.Players[0].X);
    }

    [Fact]
    public void Crate_PushedAfterHolding()
    {
        World w = MakeWorld(1, "1.C..");
        for (int i = 0; i < 40; ++i)
            Step(w, In(Dir.Right));
        Assert.Equal(Tile.Empty, w.TileAt(2, 0));
        Assert.Equal(Tile.Crate, w.TileAt(3, 0));
    }

    [Fact]
    public void Block_IsNotPushed()
    {
        World w = MakeWorld(1, "1.B..");
        for (int i = 0; i < 40; ++i)
            Step(w, In(Dir.Right));
        Assert.Equal(Tile.Block, w.TileAt(2, 0));
    }

    [Fact]
    public void Dart_PopsBombInFront()
    {
        World w = MakeWorld(1, "1......");
        w.Players[0].Darts = 1;
        AddBomb(w, 5, 0, range: 1, fuse: 80);
        Step(w, In(Dir.Right));  // 오른쪽을 바라봄
        Step(w, In(Dir.None, dart: true));
        Idle(w, 20);
        Assert.Empty(w.Bombs);
        Assert.Equal(0, w.Players[0].Darts);
    }

    [Fact]
    public void Item_PickedUp()
    {
        World w = MakeWorld(1, "1.....");
        w.Items[w.Idx(2, 0)] = ItemType.RangeUp;
        for (int i = 0; i < 30; ++i)
            Step(w, In(Dir.Right));
        Assert.Equal(2, w.Players[0].Range);
        Assert.Equal(ItemType.None, w.Items[w.Idx(2, 0)]);
    }

    [Fact]
    public void Pirate14_Parses()
    {
        MapData m = MapData.Pirate14;
        Assert.Equal(15, m.Width);
        Assert.Equal(13, m.Height);
        Assert.Equal(4, m.Spawns.Length);
        foreach (var (x, y) in m.Spawns)
            Assert.Equal(Tile.Empty, m.Tiles[y * m.Width + x]);
    }
}
