namespace BnB.Sim;

public sealed class Player
{
    public int Index;
    public int X;              // 캐릭터 중점 (단위: Rules.CellSize = 한 칸)
    public int Y;
    public Dir Facing = Dir.Down;
    public PlayerState State;
    public int TrappedTicks;
    public int InvulnTicks;
    public int BombCapacity = 1;
    public int Range = 1;
    public int Skate;
    public int Needles;
    public int Darts;
    public byte PrevButtons;   // 버튼은 "눌린 순간"만 처리하므로 이전 틱 버튼을 상태로 가진다
    public Dir PushDir;
    public int PushTicks;

    // 걸치기의 핵심: 몸이 두 칸에 걸쳐 있어도 "어느 칸에 있냐"는 중점 하나로 정한다
    public int CellX => X / Rules.CellSize;
    public int CellY => Y / Rules.CellSize;
    public int Speed => State == PlayerState.Trapped
        ? Rules.TrappedSpeed
        : Rules.BaseSpeed + Rules.SpeedPerSkate * Skate;
}

public sealed class Bomb
{
    public int Id;
    public int Owner;
    public int CellX;
    public int CellY;
    public int Fuse;
    public int Range;
    public int PassMask;       // 설치 순간 이 칸에 몸이 걸쳐 있던 플레이어는 벗어날 때까지 통과 가능
}

public sealed class Dart
{
    public int Owner;
    public int X;
    public int Y;
    public Dir Dir;
}

public sealed class World
{
    public MapData Map { get; }
    public int Width => Map.Width;
    public int Height => Map.Height;

    public int Tick { get; private set; }
    public Rng Rng;
    public Tile[] Tiles { get; }
    public ItemType[] Items { get; }
    public int[] Flames { get; }               // 칸별 물줄기 남은 틱
    public List<Bomb> Bombs { get; } = [];     // 항상 Id 오름차순
    public List<Dart> Darts { get; } = [];
    public Player[] Players { get; }
    public ResultKind Result { get; private set; }
    public int Winner { get; private set; } = -1;

    private int _nextBombId = 1;

    // 이번 틱에 일어난 일 (화면 연출·로그용. 해시에는 안 들어감)
    public List<(int X, int Y)> ExplodedThisTick { get; } = [];

    public World(MapData map, int playerCount, uint seed)
    {
        if (playerCount < 1 || playerCount > map.Spawns.Length)
            throw new ArgumentOutOfRangeException(nameof(playerCount));

        Map = map;
        Rng = new Rng(seed);
        Tiles = (Tile[])map.Tiles.Clone();
        Items = new ItemType[map.Width * map.Height];
        Flames = new int[map.Width * map.Height];
        Players = new Player[playerCount];
        for (int i = 0; i < playerCount; ++i)
        {
            var (sx, sy) = map.Spawns[i];
            Players[i] = new Player
            {
                Index = i,
                X = sx * Rules.CellSize + Rules.Half,
                Y = sy * Rules.CellSize + Rules.Half,
            };
        }
    }

    public int Idx(int x, int y) => y * Width + x;
    public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
    public Tile TileAt(int x, int y) => InBounds(x, y) ? Tiles[Idx(x, y)] : Tile.Hard;
    public Bomb? BombAt(int x, int y)
    {
        foreach (Bomb b in Bombs)
            if (b.CellX == x && b.CellY == y)
                return b;
        return null;
    }

    private static int Center(int cell) => cell * Rules.CellSize + Rules.Half;

    private bool IsBlockedFor(Player p, int x, int y)
    {
        if (TileAt(x, y) != Tile.Empty)
            return true;
        Bomb? b = BombAt(x, y);
        return b is not null && (b.PassMask & (1 << p.Index)) == 0;
    }

    // ------------------------------------------------------------------
    // 한 틱 진행. inputs[i] = i번 플레이어의 이번 틱 입력.
    // 같은 상태 + 같은 입력이면 어느 PC에서든 같은 결과가 나와야 한다.
    // ------------------------------------------------------------------
    public void Step(ReadOnlySpan<PlayerInput> inputs)
    {
        if (inputs.Length != Players.Length)
            throw new ArgumentException("input count mismatch");

        ExplodedThisTick.Clear();
        if (Result != ResultKind.Ongoing)
            return;

        // 처리 순서는 항상 플레이어 번호 순 → 같은 틱에 두 명이 같은 칸에 물풍선을 놓아도 결과가 같다
        for (int i = 0; i < Players.Length; ++i)
            HandleActions(Players[i], inputs[i]);

        for (int i = 0; i < Players.Length; ++i)
            Move(Players[i], inputs[i].Dir);

        MoveDarts();
        TickBombs();
        TickFlames();
        TickPlayers();
        PickUpItems();
        UpdatePassMasks();
        CheckResult();

        ++Tick;
    }

    private void HandleActions(Player p, PlayerInput input)
    {
        byte buttons = (byte)(input.Raw & PlayerInput.ButtonMask);
        byte pressed = (byte)(buttons & ~p.PrevButtons);
        p.PrevButtons = buttons;

        if (input.Dir != Dir.None && p.State != PlayerState.Dead)
            p.Facing = input.Dir;

        if ((pressed & PlayerInput.NeedleBit) != 0 && p.State == PlayerState.Trapped && p.Needles > 0)
        {
            --p.Needles;
            p.State = PlayerState.Alive;
            p.TrappedTicks = 0;
            p.InvulnTicks = Rules.InvulnTicks;
        }

        if (p.State != PlayerState.Alive)
            return;

        if ((pressed & PlayerInput.BombBit) != 0)
            TryPlaceBomb(p);

        if ((pressed & PlayerInput.DartBit) != 0 && p.Darts > 0)
        {
            --p.Darts;
            Darts.Add(new Dart { Owner = p.Index, X = p.X, Y = p.Y, Dir = p.Facing });
        }
    }

    private void TryPlaceBomb(Player p)
    {
        int cx = p.CellX, cy = p.CellY;
        if (TileAt(cx, cy) != Tile.Empty || BombAt(cx, cy) is not null)
            return;

        int owned = 0;
        foreach (Bomb b in Bombs)
            if (b.Owner == p.Index)
                ++owned;
        if (owned >= p.BombCapacity)
            return;

        int mask = 0;
        foreach (Player q in Players)
            if (q.State != PlayerState.Dead && Overlaps(q, cx, cy))
                mask |= 1 << q.Index;

        Bombs.Add(new Bomb
        {
            Id = _nextBombId++,
            Owner = p.Index,
            CellX = cx,
            CellY = cy,
            Fuse = Rules.FuseTicks,
            Range = p.Range,
            PassMask = mask,
        });
    }

    // 몸(한 칸 크기)이 해당 칸과 겹치는가
    private static bool Overlaps(Player p, int cx, int cy)
        => Math.Abs(p.X - Center(cx)) < Rules.CellSize && Math.Abs(p.Y - Center(cy)) < Rules.CellSize;

    // ------------------------------------------------------------------
    // 이동: 진행 축과 수직인 축은 칸 중심(레인)에 맞춘 뒤 진행한다.
    //       레인이 어긋나 있으면 모서리 보정으로 먼저 밀어 넣는다.
    // ------------------------------------------------------------------
    private void Move(Player p, Dir dir)
    {
        if (p.State == PlayerState.Dead || dir == Dir.None)
        {
            p.PushTicks = 0;
            return;
        }

        bool horizontal = dir is Dir.Left or Dir.Right;
        int d = horizontal ? dir.Dx() : dir.Dy();
        int remaining = p.Speed;

        // 축을 바꿔 써서 가로/세로를 같은 코드로 처리한다
        ref int along = ref (horizontal ? ref p.X : ref p.Y);
        ref int across = ref (horizontal ? ref p.Y : ref p.X);
        int cellAlong = along / Rules.CellSize;
        int cellAcross = across / Rules.CellSize;
        int off = across - Center(cellAcross);

        bool Blocked(int a, int c) => horizontal ? IsBlockedFor(p, a, c) : IsBlockedFor(p, c, a);

        if (off != 0)
        {
            if (!Blocked(cellAlong + d, cellAcross))
            {
                // 내 레인 앞이 비어 있으면 레인 중심으로 맞춘다
                int slide = Math.Min(remaining, Math.Abs(off));
                across -= Math.Sign(off) * slide;
                remaining -= slide;
            }
            else
            {
                // 내 레인 앞이 막혔으면, 옆 레인 쪽으로 충분히 걸쳐 있을 때만 옆 레인으로 밀어 넣는다
                int side = Math.Sign(off);
                if (Math.Abs(off) >= Rules.Half - Rules.CornerAssist
                    && !Blocked(cellAlong + d, cellAcross + side)
                    && !Blocked(cellAlong, cellAcross + side))
                {
                    int slide = Math.Min(remaining, Rules.CellSize - Math.Abs(off));
                    across += side * slide;
                    remaining -= slide;
                }
                else
                {
                    remaining = 0;
                }
            }

            if (across - Center(across / Rules.CellSize) != 0)
            {
                p.PushTicks = 0;
                return;  // 아직 레인에 안 맞음 → 이번 틱은 정렬만
            }
            cellAcross = across / Rules.CellSize;
        }

        if (remaining <= 0)
            return;

        int target = along + d * remaining;
        int newCell = target / Rules.CellSize;

        // 앞 칸이 막혀 있으면 현재 칸 중심에서 멈춘다. 뒤로 끌려가지는 않는다.
        if (Blocked(cellAlong + d, cellAcross))
        {
            int limit = Center(cellAlong);
            target = d > 0 ? Math.Max(along, Math.Min(target, limit)) : Math.Min(along, Math.Max(target, limit));
            TryPush(p, dir, cellAlong, cellAcross, horizontal, atLimit: target == limit);
        }
        else
        {
            p.PushTicks = 0;
            if (newCell != cellAlong && Blocked(newCell + d, cellAcross))
            {
                int limit = Center(newCell);
                if (d > 0 ? target > limit : target < limit)
                    target = limit;
            }
        }

        along = target;
    }

    // 상자 밀기: 상자 앞 칸 중심에 붙어서 같은 방향으로 PushTicks만큼 계속 밀면 한 칸 이동
    private void TryPush(Player p, Dir dir, int cellAlong, int cellAcross, bool horizontal, bool atLimit)
    {
        if (!atLimit || p.State != PlayerState.Alive)
        {
            p.PushTicks = 0;
            return;
        }

        int d = horizontal ? dir.Dx() : dir.Dy();
        (int cx, int cy) = horizontal ? (cellAlong + d, cellAcross) : (cellAcross, cellAlong + d);
        if (TileAt(cx, cy) != Tile.Crate || BombAt(cx, cy) is not null)
        {
            p.PushTicks = 0;
            return;
        }

        if (p.PushDir != dir)
        {
            p.PushDir = dir;
            p.PushTicks = 0;
        }
        if (++p.PushTicks < Rules.PushTicks)
            return;
        p.PushTicks = 0;

        (int tx, int ty) = horizontal ? (cellAlong + 2 * d, cellAcross) : (cellAcross, cellAlong + 2 * d);
        if (!InBounds(tx, ty) || TileAt(tx, ty) != Tile.Empty || BombAt(tx, ty) is not null
            || Items[Idx(tx, ty)] != ItemType.None)
            return;
        foreach (Player q in Players)
            if (q.State != PlayerState.Dead && Overlaps(q, tx, ty))
                return;

        Tiles[Idx(cx, cy)] = Tile.Empty;
        Tiles[Idx(tx, ty)] = Tile.Crate;
    }

    private void MoveDarts()
    {
        for (int i = 0; i < Darts.Count;)
        {
            Dart dart = Darts[i];
            dart.X += dart.Dir.Dx() * Rules.DartSpeed;
            dart.Y += dart.Dir.Dy() * Rules.DartSpeed;
            int cx = dart.X < 0 ? -1 : dart.X / Rules.CellSize;
            int cy = dart.Y < 0 ? -1 : dart.Y / Rules.CellSize;

            bool remove = false;
            if (TileAt(cx, cy) != Tile.Empty)
            {
                remove = true;
            }
            else if (BombAt(cx, cy) is { } bomb)
            {
                bomb.Fuse = 0;  // 다트에 맞은 물풍선은 이번 틱에 바로 터진다
                remove = true;
            }

            if (remove)
                Darts.RemoveAt(i);
            else
                ++i;
        }
    }

    // ------------------------------------------------------------------
    // 폭발: 연쇄는 큐로 처리하고, 큐에 넣는 순서도 Id 순으로 고정한다
    // ------------------------------------------------------------------
    private void TickBombs()
    {
        var queue = new Queue<Bomb>();
        foreach (Bomb b in Bombs)
        {
            if (b.Fuse > 0)
                --b.Fuse;
            if (b.Fuse <= 0)
                queue.Enqueue(b);
        }
        if (queue.Count == 0)
            return;

        var exploded = new HashSet<int>();
        var flameCells = new List<int>();
        var destroyed = new SortedSet<int>();

        while (queue.Count > 0)
        {
            Bomb b = queue.Dequeue();
            if (!exploded.Add(b.Id))
                continue;
            ExplodedThisTick.Add((b.CellX, b.CellY));

            flameCells.Add(Idx(b.CellX, b.CellY));
            foreach (Dir dir in (ReadOnlySpan<Dir>)[Dir.Up, Dir.Down, Dir.Left, Dir.Right])
            {
                for (int step = 1; step <= b.Range; ++step)
                {
                    int x = b.CellX + dir.Dx() * step;
                    int y = b.CellY + dir.Dy() * step;
                    Tile t = TileAt(x, y);
                    if (t == Tile.Hard)
                        break;

                    int idx = Idx(x, y);
                    flameCells.Add(idx);
                    if (t is Tile.Block or Tile.Crate)
                    {
                        destroyed.Add(idx);
                        break;
                    }
                    if (BombAt(x, y) is { } chained && !exploded.Contains(chained.Id))
                        queue.Enqueue(chained);
                }
            }
        }

        Bombs.RemoveAll(b => exploded.Contains(b.Id));

        // 물줄기에 닿은 아이템은 사라진다. 이번에 부서진 블록에서 나온 아이템은 아래에서 새로 생긴다.
        foreach (int idx in flameCells)
        {
            Flames[idx] = Rules.FlameTicks;
            if (!destroyed.Contains(idx))
                Items[idx] = ItemType.None;
        }

        // 부서지는 순서도 칸 번호 순으로 고정 → 난수를 뽑는 순서가 모든 PC에서 같다
        foreach (int idx in destroyed)
        {
            Tiles[idx] = Tile.Empty;
            Items[idx] = RollItem();
        }
    }

    private ItemType RollItem()
    {
        if (Rng.Next(100) >= Rules.ItemDropPercent)
            return ItemType.None;

        int r = Rng.Next(100);
        return r switch
        {
            < 30 => ItemType.BombUp,
            < 60 => ItemType.RangeUp,
            < 80 => ItemType.Skate,
            < 90 => ItemType.Needle,
            _ => ItemType.Dart,
        };
    }

    private void TickFlames()
    {
        foreach (Player p in Players)
        {
            if (p.State != PlayerState.Alive || p.InvulnTicks > 0)
                continue;
            // 걸치기: 몸이 물줄기 칸에 반쯤 들어가 있어도 중점 칸이 안전하면 안 갇힌다
            if (Flames[Idx(p.CellX, p.CellY)] > 0)
            {
                p.State = PlayerState.Trapped;
                p.TrappedTicks = Rules.TrapTicks;
                p.PushTicks = 0;
            }
        }

        for (int i = 0; i < Flames.Length; ++i)
            if (Flames[i] > 0)
                --Flames[i];
    }

    private void TickPlayers()
    {
        foreach (Player p in Players)
        {
            if (p.InvulnTicks > 0)
                --p.InvulnTicks;
            if (p.State == PlayerState.Trapped && --p.TrappedTicks <= 0)
                p.State = PlayerState.Dead;
        }

        // 갇힌 상대에게 닿으면 터뜨린다 (개인전)
        foreach (Player a in Players)
        {
            if (a.State != PlayerState.Alive)
                continue;
            foreach (Player t in Players)
            {
                if (t.Index == a.Index || t.State != PlayerState.Trapped)
                    continue;
                if (Math.Abs(a.X - t.X) < Rules.PopDistance && Math.Abs(a.Y - t.Y) < Rules.PopDistance)
                    t.State = PlayerState.Dead;
            }
        }
    }

    private void PickUpItems()
    {
        foreach (Player p in Players)
        {
            if (p.State != PlayerState.Alive)
                continue;
            int idx = Idx(p.CellX, p.CellY);
            ItemType item = Items[idx];
            if (item == ItemType.None)
                continue;
            Items[idx] = ItemType.None;
            switch (item)
            {
                case ItemType.BombUp: p.BombCapacity = Math.Min(Rules.MaxBombs, p.BombCapacity + 1); break;
                case ItemType.RangeUp: p.Range = Math.Min(Rules.MaxRange, p.Range + 1); break;
                case ItemType.Skate: p.Skate = Math.Min(Rules.MaxSkate, p.Skate + 1); break;
                case ItemType.Needle: p.Needles = Math.Min(Rules.MaxHeld, p.Needles + 1); break;
                case ItemType.Dart: p.Darts = Math.Min(Rules.MaxHeld, p.Darts + 1); break;
            }
        }
    }

    private void UpdatePassMasks()
    {
        foreach (Bomb b in Bombs)
        {
            if (b.PassMask == 0)
                continue;
            foreach (Player p in Players)
                if ((b.PassMask & (1 << p.Index)) != 0 && !Overlaps(p, b.CellX, b.CellY))
                    b.PassMask &= ~(1 << p.Index);
        }
    }

    private void CheckResult()
    {
        int notDead = 0, alive = 0, last = -1;
        foreach (Player p in Players)
        {
            if (p.State == PlayerState.Dead)
                continue;
            ++notDead;
            if (p.State == PlayerState.Alive)
            {
                ++alive;
                last = p.Index;
            }
        }

        if (Players.Length == 1)
        {
            // 혼자 하는 연습 모드: 죽으면 끝
            if (notDead == 0)
                Result = ResultKind.Draw;
        }
        else if (notDead == 0)
        {
            Result = ResultKind.Draw;
        }
        else if (notDead == 1 && alive == 1)
        {
            Result = ResultKind.Win;
            Winner = last;
        }

        if (Result == ResultKind.Ongoing && Tick + 1 >= Rules.TimeLimitTicks)
            Result = ResultKind.Draw;
    }

    // ------------------------------------------------------------------
    // 상태 해시 (FNV-1a 64). 클라와 서버가 같은 틱에 같은 값이어야 한다.
    // string.GetHashCode / HashCode.Combine 은 프로세스마다 값이 달라서 쓰면 안 된다.
    // ------------------------------------------------------------------
    public ulong ComputeHash()
    {
        var h = new Fnv64();
        h.Add(Tick);
        h.Add((int)Rng.State);
        h.Add((int)Result);
        h.Add(Winner);
        h.Add(_nextBombId);
        for (int i = 0; i < Tiles.Length; ++i)
        {
            h.Add((byte)Tiles[i]);
            h.Add((byte)Items[i]);
            h.Add(Flames[i]);
        }
        h.Add(Bombs.Count);
        foreach (Bomb b in Bombs)
        {
            h.Add(b.Id); h.Add(b.Owner); h.Add(b.CellX); h.Add(b.CellY);
            h.Add(b.Fuse); h.Add(b.Range); h.Add(b.PassMask);
        }
        h.Add(Darts.Count);
        foreach (Dart d in Darts)
        {
            h.Add(d.Owner); h.Add(d.X); h.Add(d.Y); h.Add((byte)d.Dir);
        }
        foreach (Player p in Players)
        {
            h.Add(p.X); h.Add(p.Y); h.Add((byte)p.Facing); h.Add((byte)p.State);
            h.Add(p.TrappedTicks); h.Add(p.InvulnTicks); h.Add(p.BombCapacity); h.Add(p.Range);
            h.Add(p.Skate); h.Add(p.Needles); h.Add(p.Darts); h.Add(p.PrevButtons);
            h.Add((byte)p.PushDir); h.Add(p.PushTicks);
        }
        return h.Value;
    }

    private struct Fnv64
    {
        public ulong Value;

        public Fnv64() => Value = 14695981039346656037UL;

        public void Add(byte b)
        {
            Value ^= b;
            Value *= 1099511628211UL;
        }

        public void Add(int v)
        {
            Add((byte)v);
            Add((byte)(v >> 8));
            Add((byte)(v >> 16));
            Add((byte)(v >> 24));
        }
    }
}
