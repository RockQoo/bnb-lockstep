namespace BnB.Sim;

public sealed class MapData
{
    public required string Id { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required Tile[] Tiles { get; init; }
    public required (int X, int Y)[] Spawns { get; init; }

    // 크레이지아케이드 해적 14 (docs/pirate14-reference.png 를 칸 단위로 옮김)
    //   H = 기둥·배 (고정, 안 부서짐)   B = 노란 블록 (고정, 부서짐)   C = 나무 상자 (밀림, 부서짐)   . = 빈 칸
    //   1~4 = 시작 위치 (빈 칸)
    private static readonly string[] Pirate14Rows =
    [
        "BBBB...B...BBBB",
        "BHB.CCC.CCC.BHB",
        "BB.C.B.C.B.C.BB",
        "B.C.BBBHBBB.C.B",
        "B1CBBBBHBBBBC2B",
        "B.C.BBHHHBB.C.B",
        "B.CBBBHHHBBBC.B",
        "BB.C.BBBBB.C.BB",
        "BBB.CBBBBBC.BBB",
        "BBBB.C.B.C.BBBB",
        "BB.BB.CCC.BB.BB",
        "BH.BBB...BBB.HB",
        "B3.BBBBBBBBB.4B",
    ];

    public static MapData Pirate14 { get; } = Parse("pirate14", Pirate14Rows);

    public static MapData Get(string id) => id switch
    {
        "pirate14" => Pirate14,
        _ => throw new ArgumentException($"unknown map: {id}"),
    };

    public static MapData Parse(string id, string[] rows)
    {
        int h = rows.Length;
        int w = rows[0].Length;
        var tiles = new Tile[w * h];
        var spawns = new (int, int)[4];
        int spawnCount = 0;

        for (int y = 0; y < h; ++y)
        {
            if (rows[y].Length != w)
                throw new ArgumentException($"row {y} width mismatch");

            for (int x = 0; x < w; ++x)
            {
                char ch = rows[y][x];
                tiles[y * w + x] = ch switch
                {
                    'H' => Tile.Hard,
                    'B' => Tile.Block,
                    'C' => Tile.Crate,
                    _ => Tile.Empty,
                };
                if (ch is >= '1' and <= '4')
                {
                    spawns[ch - '1'] = (x, y);
                    ++spawnCount;
                }
            }
        }

        return new MapData
        {
            Id = id,
            Width = w,
            Height = h,
            Tiles = tiles,
            Spawns = spawns[..spawnCount],
        };
    }
}
