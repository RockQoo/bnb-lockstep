using System;
using System.Collections.Generic;
using System.Linq;
using BnB.Sim;

namespace BnB.Bot;

// 간단한 AI. 칸 단위로 판단한다.
//   1) 위험한 칸(물풍선 폭발 범위, 물줄기)에 있으면 가장 가까운 안전한 칸으로 도망
//   2) 옆에 부술 블록이나 상대가 있고, 놓아도 도망칠 곳이 있으면 물풍선
//   3) 아니면 아이템 → 부술 블록 → 상대 순으로 이동
// 봇의 판단은 결정론일 필요가 없다. 봇이 보낸 "입력"이 서버에서 확정되고, 그 입력만 모두에게 전달되기 때문이다.
public sealed class BotBrain
{
    private readonly Random _random;
    private int _wanderUntil;
    private Dir _wanderDir;

    public BotBrain(int seed) => _random = new Random(seed);

    public byte Decide(World w, int me)
    {
        Player p = w.Players[me];
        if (p.State == PlayerState.Dead)
            return 0;
        if (p.State == PlayerState.Trapped)
            return p.Needles > 0 && _random.Next(10) == 0 ? (byte)(PlayerInput.NeedleBit) : (byte)0;

        bool[] danger = DangerMap(w, extraBomb: null, extraRange: 0);
        int mx = p.CellX, my = p.CellY;

        if (danger[w.Idx(mx, my)])
        {
            Dir flee = PathDir(w, p, (x, y) => !danger[w.Idx(x, y)], avoid: null);
            return (byte)flee;
        }

        // 다트: 같은 줄에 상대 물풍선... 대신 상대가 일직선 가까이 있으면 가끔 던진다
        if (p.Darts > 0 && _random.Next(40) == 0)
            return (byte)((byte)p.Facing | PlayerInput.DartBit);

        if (ShouldBomb(w, p) && CanEscapeAfterBomb(w, p))
            return PlayerInput.BombBit;

        Dir dir = PathDir(w, p, (x, y) => w.Items[w.Idx(x, y)] != ItemType.None, danger);
        if (dir == Dir.None)
            dir = PathDir(w, p, (x, y) => NextToBreakable(w, x, y) || EnemyNear(w, me, x, y), danger);
        if (dir == Dir.None)
        {
            if (w.Tick >= _wanderUntil)
            {
                _wanderDir = (Dir)_random.Next(1, 5);
                _wanderUntil = w.Tick + 15;
            }
            int nx = mx + _wanderDir.Dx(), ny = my + _wanderDir.Dy();
            dir = Walkable(w, p, nx, ny) && !danger[w.Idx(nx, ny)] ? _wanderDir : Dir.None;
        }
        return (byte)dir;
    }

    // 남아 있는 물줄기 칸은 지나가면 갇히므로 못 가는 칸으로 본다
    private static bool Walkable(World w, Player p, int x, int y)
        => w.InBounds(x, y) && w.TileAt(x, y) == Tile.Empty
           && w.Flames[w.Idx(x, y)] == 0
           && (w.BombAt(x, y) is not { } b || (b.PassMask & (1 << p.Index)) != 0);

    private static bool NextToBreakable(World w, int x, int y)
    {
        foreach ((int dx, int dy) in Neighbors)
        {
            Tile t = w.TileAt(x + dx, y + dy);
            if (t is Tile.Block or Tile.Crate)
                return true;
        }
        return false;
    }

    private static bool EnemyNear(World w, int me, int x, int y)
    {
        foreach (Player q in w.Players)
            if (q.Index != me && q.State != PlayerState.Dead && Math.Abs(q.CellX - x) + Math.Abs(q.CellY - y) <= 1)
                return true;
        return false;
    }

    private static readonly (int, int)[] Neighbors = [(0, -1), (0, 1), (-1, 0), (1, 0)];

    private bool ShouldBomb(World w, Player p)
    {
        int owned = w.Bombs.Count(b => b.Owner == p.Index);
        if (owned >= p.BombCapacity || w.BombAt(p.CellX, p.CellY) is not null)
            return false;
        if (NextToBreakable(w, p.CellX, p.CellY))
            return _random.Next(3) == 0;

        // 상대가 내 물줄기 범위의 일직선 위에 있으면
        foreach (Player q in w.Players)
        {
            if (q.Index == p.Index || q.State == PlayerState.Dead)
                continue;
            if (q.State == PlayerState.Trapped)
                continue;
            bool sameRow = q.CellY == p.CellY && Math.Abs(q.CellX - p.CellX) <= p.Range;
            bool sameCol = q.CellX == p.CellX && Math.Abs(q.CellY - p.CellY) <= p.Range;
            if (sameRow || sameCol)
                return _random.Next(2) == 0;
        }
        return false;
    }

    private bool CanEscapeAfterBomb(World w, Player p)
    {
        bool[] danger = DangerMap(w, extraBomb: (p.CellX, p.CellY), extraRange: p.Range);
        // 지금 칸의 물풍선은 내가 통과할 수 있으니, 지금 칸에서 출발해 안전한 칸까지 길이 있는지 본다
        var dist = Bfs(w, p, p.CellX, p.CellY, avoid: null, extraBlock: (p.CellX, p.CellY));
        int fuseCells = Rules.FuseTicks * p.Speed / Rules.CellSize - 1;  // 터지기 전에 걸을 수 있는 칸 수 (여유 1칸)
        for (int i = 0; i < dist.Length; ++i)
            if (dist[i] >= 0 && dist[i] <= fuseCells && !danger[i])
                return true;
        return false;
    }

    // 모든 물풍선의 폭발 범위 + 현재 물줄기
    private static bool[] DangerMap(World w, (int X, int Y)? extraBomb, int extraRange)
    {
        var danger = new bool[w.Width * w.Height];
        for (int i = 0; i < danger.Length; ++i)
            danger[i] = w.Flames[i] > 0;

        void Mark(int bx, int by, int range)
        {
            danger[w.Idx(bx, by)] = true;
            foreach ((int dx, int dy) in Neighbors)
            {
                for (int s = 1; s <= range; ++s)
                {
                    int x = bx + dx * s, y = by + dy * s;
                    Tile t = w.TileAt(x, y);
                    if (t == Tile.Hard)
                        break;
                    danger[w.Idx(x, y)] = true;
                    if (t != Tile.Empty)
                        break;
                }
            }
        }

        foreach (Bomb b in w.Bombs)
            Mark(b.CellX, b.CellY, b.Range);
        if (extraBomb is { } e)
            Mark(e.X, e.Y, extraRange);
        return danger;
    }

    private static int[] Bfs(World w, Player p, int sx, int sy, bool[]? avoid, (int X, int Y)? extraBlock)
    {
        var dist = new int[w.Width * w.Height];
        Array.Fill(dist, -1);
        var q = new Queue<(int, int)>();
        dist[w.Idx(sx, sy)] = 0;
        q.Enqueue((sx, sy));
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            foreach ((int dx, int dy) in Neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (!w.InBounds(nx, ny) || dist[w.Idx(nx, ny)] >= 0)
                    continue;
                if (!Walkable(w, p, nx, ny) || (extraBlock is { } eb && eb.X == nx && eb.Y == ny))
                    continue;
                if (avoid is not null && avoid[w.Idx(nx, ny)])
                    continue;
                dist[w.Idx(nx, ny)] = dist[w.Idx(x, y)] + 1;
                q.Enqueue((nx, ny));
            }
        }
        return dist;
    }

    // goal을 만족하는 가장 가까운 칸으로 가는 첫 방향
    private static Dir PathDir(World w, Player p, Func<int, int, bool> goal, bool[]? avoid)
    {
        int sx = p.CellX, sy = p.CellY;
        var prev = new int[w.Width * w.Height];
        Array.Fill(prev, -2);
        var q = new Queue<(int, int)>();
        prev[w.Idx(sx, sy)] = -1;
        q.Enqueue((sx, sy));
        int found = -1;
        while (q.Count > 0 && found < 0)
        {
            var (x, y) = q.Dequeue();
            if ((x != sx || y != sy) && goal(x, y))
            {
                found = w.Idx(x, y);
                break;
            }
            foreach ((int dx, int dy) in Neighbors)
            {
                int nx = x + dx, ny = y + dy;
                if (!w.InBounds(nx, ny) || prev[w.Idx(nx, ny)] != -2 || !Walkable(w, p, nx, ny))
                    continue;
                if (avoid is not null && avoid[w.Idx(nx, ny)])
                    continue;
                prev[w.Idx(nx, ny)] = w.Idx(x, y);
                q.Enqueue((nx, ny));
            }
        }
        if (found < 0)
            return Dir.None;

        int start = w.Idx(sx, sy);
        int cur = found;
        while (prev[cur] != start)
            cur = prev[cur];
        int cx = cur % w.Width, cy = cur / w.Width;
        if (cx < sx) return Dir.Left;
        if (cx > sx) return Dir.Right;
        if (cy < sy) return Dir.Up;
        return Dir.Down;
    }
}
