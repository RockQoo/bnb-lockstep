namespace BnB.Sim;

// 시뮬레이션은 정수만 쓴다. float/double이 들어오면 PC마다 결과가 달라질 수 있다(결정론 붕괴).

public enum Tile : byte
{
    Empty = 0,
    Hard = 1,   // 안 움직이고 안 부서짐 (기둥, 배)
    Block = 2,  // 안 움직이지만 부서짐 (노란 블록)
    Crate = 3,  // 밀 수 있고 부서짐 (나무 상자)
}

public enum ItemType : byte
{
    None = 0,
    BombUp = 1,   // 물풍선 개수 +1
    RangeUp = 2,  // 물줄기 길이 +1
    Skate = 3,    // 이동 속도 +1
    Needle = 4,   // 바늘: 물방울에 갇혔을 때 탈출
    Dart = 5,     // 다트: 바라보는 방향으로 던져 물풍선을 터뜨림
}

public enum PlayerState : byte
{
    Alive = 0,
    Trapped = 1,  // 물방울에 갇힘
    Dead = 2,
}

public enum Dir : byte
{
    None = 0,
    Up = 1,
    Down = 2,
    Left = 3,
    Right = 4,
}

public enum ResultKind : byte
{
    Ongoing = 0,
    Win = 1,
    Draw = 2,
}

public static class DirExt
{
    public static int Dx(this Dir d) => d switch { Dir.Left => -1, Dir.Right => 1, _ => 0 };
    public static int Dy(this Dir d) => d switch { Dir.Up => -1, Dir.Down => 1, _ => 0 };
}

public static class Rules
{
    public const int TickRate = 30;
    public const int CellSize = 1000;              // 한 칸 = 정수 1000 단위 (고정소수점)
    public const int Half = CellSize / 2;

    public const int FuseTicks = 90;               // 물풍선 3초 후 폭발
    public const int FlameTicks = 15;              // 물줄기 0.5초 유지
    public const int TrapTicks = 150;              // 물방울 5초 지나면 사망
    public const int InvulnTicks = 45;             // 바늘 탈출 직후 1.5초 무적
    public const int TimeLimitTicks = TickRate * 180;

    public const int BaseSpeed = 70;               // 단위/틱 (초당 약 2.1칸)
    public const int SpeedPerSkate = 20;
    public const int MaxSkate = 5;                 // 최대 170 단위/틱 < Half (한 틱에 칸 경계를 두 번 넘지 않음)
    public const int TrappedSpeed = 12;

    public const int MaxBombs = 6;
    public const int MaxRange = 7;
    public const int MaxHeld = 3;                  // 바늘·다트 최대 소지 수

    public const int CornerAssist = 300;           // 모서리 보정: 옆 칸 중심에서 이만큼 안쪽이면 골목으로 밀어 넣음
    public const int PushTicks = 8;                // 상자를 이만큼 계속 밀면 한 칸 이동
    public const int PopDistance = 600;            // 갇힌 상대와 이 거리 안으로 겹치면 터뜨림
    public const int DartSpeed = 250;              // 단위/틱

    public const int ItemDropPercent = 55;
}
