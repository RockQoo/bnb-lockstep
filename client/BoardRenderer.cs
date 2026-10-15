using BnB.Sim;
using Godot;

namespace BnB.Client;

// Sim 상태를 읽어서 그리기만 한다. Sim 상태를 바꾸지 않는다.
// 정수 좌표 → float 변환은 여기(화면)에서만 일어난다. float는 Sim 안으로 들어가지 않는다.
public sealed class BoardRenderer
{
    public const float CellPx = 44f;
    public static readonly Vector2 Origin = new(16, 40);

    public static readonly Color[] PlayerColors =
    [
        new(0.92f, 0.22f, 0.22f),  // P1 빨강
        new(0.20f, 0.72f, 0.30f),  // P2 초록
        new(0.98f, 0.62f, 0.10f),  // P3 주황
        new(0.68f, 0.32f, 0.92f),  // P4 보라
    ];

    private static readonly Color FloorA = new(0.86f, 0.74f, 0.50f);
    private static readonly Color FloorB = new(0.82f, 0.70f, 0.46f);
    private static readonly Color HardColor = new(0.33f, 0.34f, 0.40f);
    private static readonly Color HardEdge = new(0.20f, 0.21f, 0.26f);
    private static readonly Color BlockColor = new(0.98f, 0.78f, 0.22f);
    private static readonly Color BlockShade = new(0.78f, 0.52f, 0.12f);
    private static readonly Color CrateColor = new(0.80f, 0.63f, 0.40f);
    private static readonly Color CrateLine = new(0.52f, 0.36f, 0.18f);
    private static readonly Color BombColor = new(0.16f, 0.30f, 0.85f);
    private static readonly Color FlameColor = new(0.20f, 0.62f, 1.00f, 0.88f);
    private static readonly Color FlameCore = new(0.75f, 0.92f, 1.00f, 0.95f);

    private readonly Font _font = ThemeDB.FallbackFont;

    public static Vector2 ToScreen(float simX, float simY)
        => Origin + new Vector2(simX, simY) * (CellPx / Rules.CellSize);

    private static Rect2 CellRect(int x, int y, float inset = 0)
        => new(Origin + new Vector2(x * CellPx + inset, y * CellPx + inset), new Vector2(CellPx - 2 * inset, CellPx - 2 * inset));

    private static Vector2 CellCenter(int x, int y) => Origin + new Vector2((x + 0.5f) * CellPx, (y + 0.5f) * CellPx);

    public void Draw(CanvasItem c, World w, Vector2[] playerPos, int localPlayer, double timeSec)
    {
        // 바닥
        c.DrawRect(new Rect2(Origin - new Vector2(4, 4), new Vector2(w.Width * CellPx + 8, w.Height * CellPx + 8)), HardEdge);
        for (int y = 0; y < w.Height; ++y)
            for (int x = 0; x < w.Width; ++x)
                c.DrawRect(CellRect(x, y), ((x + y) & 1) == 0 ? FloorA : FloorB);

        // 물줄기 (바닥 위, 오브젝트 아래)
        for (int y = 0; y < w.Height; ++y)
        {
            for (int x = 0; x < w.Width; ++x)
            {
                int f = w.Flames[w.Idx(x, y)];
                if (f <= 0)
                    continue;
                float t = f / (float)Rules.FlameTicks;
                c.DrawRect(CellRect(x, y, 2), FlameColor with { A = 0.5f + 0.4f * t });
                c.DrawRect(CellRect(x, y, CellPx * 0.3f), FlameCore with { A = 0.4f + 0.5f * t });
            }
        }

        // 아이템
        for (int y = 0; y < w.Height; ++y)
            for (int x = 0; x < w.Width; ++x)
                if (w.Items[w.Idx(x, y)] is var item and not ItemType.None && w.Tiles[w.Idx(x, y)] == Tile.Empty)
                    DrawItem(c, item, CellCenter(x, y), timeSec);

        // 지형
        for (int y = 0; y < w.Height; ++y)
        {
            for (int x = 0; x < w.Width; ++x)
            {
                switch (w.Tiles[w.Idx(x, y)])
                {
                    case Tile.Hard:
                        c.DrawRect(CellRect(x, y, 1), HardColor);
                        c.DrawRect(CellRect(x, y, 1), HardEdge, false, 2);
                        c.DrawCircle(CellCenter(x, y), CellPx * 0.18f, HardEdge);
                        break;
                    case Tile.Block:
                    {
                        Rect2 r = CellRect(x, y, 2);
                        c.DrawRect(r, BlockShade);
                        c.DrawRect(new Rect2(r.Position, new Vector2(r.Size.X, r.Size.Y * 0.72f)), BlockColor);
                        c.DrawRect(r, BlockShade, false, 1.5f);
                        break;
                    }
                    case Tile.Crate:
                    {
                        Rect2 r = CellRect(x, y, 4);
                        c.DrawRect(r, CrateColor);
                        c.DrawRect(r, CrateLine, false, 2.5f);
                        c.DrawLine(r.Position, r.End, CrateLine, 2);
                        c.DrawLine(new Vector2(r.End.X, r.Position.Y), new Vector2(r.Position.X, r.End.Y), CrateLine, 2);
                        break;
                    }
                }
            }
        }

        // 물풍선
        foreach (Bomb b in w.Bombs)
        {
            float pulse = 1f + 0.06f * Mathf.Sin((float)timeSec * 10f + b.Id);
            float urgency = 1f - b.Fuse / (float)Rules.FuseTicks;
            Vector2 p = CellCenter(b.CellX, b.CellY);
            float r = CellPx * 0.36f * pulse;
            c.DrawCircle(p, r, BombColor.Lerp(new Color(0.55f, 0.25f, 0.95f), urgency * 0.5f));
            c.DrawCircle(p + new Vector2(-r * 0.35f, -r * 0.35f), r * 0.25f, new Color(1, 1, 1, 0.7f));
            c.DrawArc(p, r, 0, Mathf.Tau, 24, PlayerColors[b.Owner % PlayerColors.Length], 2);
        }

        // 다트
        foreach (Dart d in w.Darts)
        {
            Vector2 p = ToScreen(d.X, d.Y);
            DrawArrow(c, p, d.Dir, CellPx * 0.22f, new Color(0.15f, 0.15f, 0.15f));
        }

        // 플레이어
        foreach (Player pl in w.Players)
            DrawPlayer(c, pl, playerPos[pl.Index], pl.Index == localPlayer, timeSec);
    }

    private void DrawPlayer(CanvasItem c, Player pl, Vector2 simPos, bool isLocal, double timeSec)
    {
        Vector2 center = ToScreen(simPos.X, simPos.Y);
        Color color = PlayerColors[pl.Index % PlayerColors.Length];
        float size = CellPx * 0.70f;
        var rect = new Rect2(center - new Vector2(size, size) / 2, new Vector2(size, size));

        if (pl.State == PlayerState.Dead)
        {
            c.DrawRect(rect, new Color(0.4f, 0.4f, 0.4f, 0.55f));
            c.DrawLine(rect.Position, rect.End, new Color(0.15f, 0.15f, 0.15f), 3);
            c.DrawLine(new Vector2(rect.End.X, rect.Position.Y), new Vector2(rect.Position.X, rect.End.Y), new Color(0.15f, 0.15f, 0.15f), 3);
            return;
        }

        bool blink = pl.InvulnTicks > 0 && ((int)(timeSec * 12) & 1) == 0;
        Color body = blink ? color with { A = 0.35f } : color;
        c.DrawRect(rect, body);
        c.DrawRect(rect, isLocal ? Colors.White : new Color(0.1f, 0.1f, 0.1f), false, isLocal ? 3 : 1.5f);

        // 바라보는 방향
        Vector2 dirV = new(pl.Facing.Dx(), pl.Facing.Dy());
        DrawArrow(c, center + dirV * size * 0.62f, pl.Facing, CellPx * 0.12f, color.Darkened(0.4f));

        c.DrawString(_font, center + new Vector2(-size / 2, 5), $"P{pl.Index + 1}", HorizontalAlignment.Center, size, 14, Colors.White);

        if (pl.State == PlayerState.Trapped)
        {
            // 물방울 + 남은 시간
            float r = CellPx * 0.62f;
            c.DrawCircle(center, r, new Color(0.55f, 0.85f, 1f, 0.45f));
            c.DrawArc(center, r, 0, Mathf.Tau, 32, new Color(0.8f, 0.95f, 1f, 0.9f), 2);
            float left = pl.TrappedTicks / (float)Rules.TrapTicks;
            c.DrawArc(center, r + 4, -Mathf.Pi / 2, -Mathf.Pi / 2 + Mathf.Tau * left, 32, new Color(1f, 0.3f, 0.3f), 3);
        }
    }

    private static void DrawArrow(CanvasItem c, Vector2 tip, Dir dir, float size, Color color)
    {
        Vector2 d = new(dir.Dx(), dir.Dy());
        if (d == Vector2.Zero)
            d = Vector2.Down;
        Vector2 side = new(-d.Y, d.X);
        c.DrawColoredPolygon([tip + d * size, tip - d * size * 0.6f + side * size * 0.8f, tip - d * size * 0.6f - side * size * 0.8f], color);
    }

    private void DrawItem(CanvasItem c, ItemType item, Vector2 p, double timeSec)
    {
        float bob = Mathf.Sin((float)timeSec * 4f + p.X * 0.1f) * 2f;
        p += new Vector2(0, bob);
        float r = CellPx * 0.34f;
        c.DrawCircle(p, r, new Color(1f, 1f, 1f, 0.92f));
        c.DrawArc(p, r, 0, Mathf.Tau, 24, new Color(0.3f, 0.3f, 0.3f), 1.5f);

        switch (item)
        {
            case ItemType.BombUp:
                c.DrawCircle(p + new Vector2(-2, 2), r * 0.5f, BombColor);
                c.DrawString(_font, p + new Vector2(0, -1), "+", HorizontalAlignment.Left, -1, 14, new Color(0.1f, 0.1f, 0.1f));
                break;
            case ItemType.RangeUp:
                c.DrawRect(new Rect2(p - new Vector2(r * 0.7f, r * 0.18f), new Vector2(r * 1.4f, r * 0.36f)), FlameColor);
                c.DrawRect(new Rect2(p - new Vector2(r * 0.18f, r * 0.7f), new Vector2(r * 0.36f, r * 1.4f)), FlameColor);
                break;
            case ItemType.Skate:
                c.DrawString(_font, p + new Vector2(-r, 5), "S", HorizontalAlignment.Center, r * 2, 15, new Color(0.1f, 0.55f, 0.2f));
                c.DrawLine(p + new Vector2(-r * 0.6f, r * 0.55f), p + new Vector2(r * 0.6f, r * 0.55f), new Color(0.1f, 0.55f, 0.2f), 2);
                break;
            case ItemType.Needle:
                // 바늘: 대각선 은색 선 + 바늘귀
                c.DrawLine(p + new Vector2(-r * 0.65f, r * 0.65f), p + new Vector2(r * 0.65f, -r * 0.65f), new Color(0.45f, 0.48f, 0.55f), 3);
                c.DrawArc(p + new Vector2(r * 0.55f, -r * 0.55f), r * 0.16f, 0, Mathf.Tau, 12, new Color(0.25f, 0.25f, 0.3f), 1.5f);
                break;
            case ItemType.Dart:
                // 다트: 화살
                c.DrawLine(p + new Vector2(-r * 0.6f, 0), p + new Vector2(r * 0.3f, 0), new Color(0.2f, 0.2f, 0.2f), 2.5f);
                DrawArrow(c, p + new Vector2(r * 0.35f, 0), Dir.Right, r * 0.35f, new Color(0.85f, 0.15f, 0.15f));
                c.DrawLine(p + new Vector2(-r * 0.65f, -r * 0.25f), p + new Vector2(-r * 0.45f, 0), new Color(0.85f, 0.15f, 0.15f), 2);
                c.DrawLine(p + new Vector2(-r * 0.65f, r * 0.25f), p + new Vector2(-r * 0.45f, 0), new Color(0.85f, 0.15f, 0.15f), 2);
                break;
        }
    }
}
