namespace BnB.Sim;

// 한 틱의 입력 = 1바이트.
//   bit 0~2 : 방향 (Dir)
//   bit 3   : 물풍선
//   bit 4   : 바늘
//   bit 5   : 다트
// 락스텝은 상태가 아니라 이 바이트만 주고받는다.
public readonly record struct PlayerInput(byte Raw)
{
    public const byte BombBit = 1 << 3;
    public const byte NeedleBit = 1 << 4;
    public const byte DartBit = 1 << 5;
    public const byte ButtonMask = BombBit | NeedleBit | DartBit;

    public Dir Dir => (Dir)(Raw & 0x7);
    public bool Bomb => (Raw & BombBit) != 0;
    public bool Needle => (Raw & NeedleBit) != 0;
    public bool Dart => (Raw & DartBit) != 0;

    public static PlayerInput Make(Dir dir, bool bomb = false, bool needle = false, bool dart = false)
    {
        int raw = (int)dir;
        if (bomb) raw |= BombBit;
        if (needle) raw |= NeedleBit;
        if (dart) raw |= DartBit;
        return new PlayerInput((byte)raw);
    }
}
