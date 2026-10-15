namespace BnB.Sim;

// 직접 구현한 xorshift32.
// System.Random은 .NET 버전에 따라 구현이 바뀔 수 있어서, 시드가 같으면 어디서든 같은 수열이 나오도록 직접 만든다.
public struct Rng
{
    public uint State;

    public Rng(uint seed)
    {
        State = seed == 0 ? 0x9E3779B9u : seed;
    }

    public uint NextU32()
    {
        uint x = State;
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        State = x;
        return x;
    }

    // [0, max)
    public int Next(int max) => (int)(NextU32() % (uint)max);
}
