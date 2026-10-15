namespace BnB.Sim;

// 리플레이 = 맵 + 인원 + 시드 + 틱별 입력. 상태는 저장하지 않는다.
// 결정론이 지켜지면 이것만으로 판 전체가 똑같이 재생되고, 마지막 해시로 그걸 검증한다.
public sealed class Replay
{
    private const uint Magic = 0x52424E42;  // "BNBR"
    private const ushort Version = 1;

    public required string MapId { get; init; }
    public required int PlayerCount { get; init; }
    public required uint Seed { get; init; }
    public List<byte[]> Frames { get; } = [];  // Frames[tick][player]
    public ulong FinalHash { get; set; }

    public World CreateWorld() => new(MapData.Get(MapId), PlayerCount, Seed);

    public static void StepWorld(World world, byte[] frame)
    {
        Span<PlayerInput> inputs = stackalloc PlayerInput[frame.Length];
        for (int i = 0; i < frame.Length; ++i)
            inputs[i] = new PlayerInput(frame[i]);
        world.Step(inputs);
    }

    // 처음부터 끝까지 다시 돌려서 마지막 해시가 기록과 같은지 확인
    public bool Verify(out ulong actual)
    {
        World world = CreateWorld();
        foreach (byte[] frame in Frames)
            StepWorld(world, frame);
        actual = world.ComputeHash();
        return actual == FinalHash;
    }

    public void Write(Stream stream)
    {
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);
        w.Write(MapId);
        w.Write((byte)PlayerCount);
        w.Write(Seed);
        w.Write(Frames.Count);
        foreach (byte[] f in Frames)
            w.Write(f);
        w.Write(FinalHash);
    }

    public static Replay Read(Stream stream)
    {
        using var r = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic)
            throw new InvalidDataException("not a BnB replay");
        ushort version = r.ReadUInt16();
        if (version != Version)
            throw new InvalidDataException($"unsupported replay version {version}");

        var replay = new Replay
        {
            MapId = r.ReadString(),
            PlayerCount = r.ReadByte(),
            Seed = r.ReadUInt32(),
        };
        int count = r.ReadInt32();
        for (int i = 0; i < count; ++i)
            replay.Frames.Add(r.ReadBytes(replay.PlayerCount));
        replay.FinalHash = r.ReadUInt64();
        return replay;
    }

    public void Save(string path)
    {
        using FileStream fs = File.Create(path);
        Write(fs);
    }

    public static Replay Load(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return Read(fs);
    }
}
