using System.Text;

namespace BnB.Net;

// 패킷 = [u16 길이][u8 타입][본문]   (길이 = 타입 1바이트 + 본문)
public enum MsgType : byte
{
    Hello = 1,     // C→S  이름
    Welcome = 2,   // S→C  내 번호, 인원, 시드, 맵
    Lobby = 3,     // S→C  접속 인원 / 필요 인원
    Start = 4,     // S→C  게임 시작
    Input = 5,     // C→S  입력 1바이트
    Frame = 6,     // S→C  틱 번호 + 모든 플레이어 입력  ← 락스텝의 핵심 메시지
    Hash = 7,      // C→S  틱 번호 + 상태 해시 (디싱크 검사)
    Desync = 8,    // S→C  디싱크 발생 알림
    GameOver = 9,  // S→C  결과 + 마지막 해시
}

public readonly record struct WelcomeMsg(int PlayerIndex, int PlayerCount, uint Seed, string MapId);
public readonly record struct LobbyMsg(int Connected, int Needed);
public readonly record struct FrameMsg(int Tick, byte[] Inputs);
public readonly record struct HashMsg(int Tick, ulong Hash);
public readonly record struct DesyncMsg(int Tick, int Player);
public readonly record struct GameOverMsg(byte Result, int Winner, int Tick, ulong FinalHash);

public static class Protocol
{
    public const int DefaultPort = 7777;
    public const int HashInterval = 30;  // 30틱(1초)마다 해시 보고

    private static byte[] Build(MsgType type, Action<BinaryWriter>? body = null)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);
            w.Write((byte)type);
            body?.Invoke(w);
        }
        byte[] bytes = ms.ToArray();
        int len = bytes.Length - 2;
        bytes[0] = (byte)len;
        bytes[1] = (byte)(len >> 8);
        return bytes;
    }

    public static byte[] Hello(string name) => Build(MsgType.Hello, w => w.Write(name));
    public static byte[] Welcome(WelcomeMsg m) => Build(MsgType.Welcome, w =>
    {
        w.Write((byte)m.PlayerIndex);
        w.Write((byte)m.PlayerCount);
        w.Write(m.Seed);
        w.Write(m.MapId);
    });
    public static byte[] Lobby(LobbyMsg m) => Build(MsgType.Lobby, w => { w.Write((byte)m.Connected); w.Write((byte)m.Needed); });
    public static byte[] Start() => Build(MsgType.Start);
    public static byte[] Input(byte raw) => Build(MsgType.Input, w => w.Write(raw));
    public static byte[] Frame(FrameMsg m) => Build(MsgType.Frame, w => { w.Write(m.Tick); w.Write(m.Inputs); });
    public static byte[] Hash(HashMsg m) => Build(MsgType.Hash, w => { w.Write(m.Tick); w.Write(m.Hash); });
    public static byte[] Desync(DesyncMsg m) => Build(MsgType.Desync, w => { w.Write(m.Tick); w.Write((byte)m.Player); });
    public static byte[] GameOver(GameOverMsg m) => Build(MsgType.GameOver, w =>
    {
        w.Write(m.Result);
        w.Write((sbyte)m.Winner);
        w.Write(m.Tick);
        w.Write(m.FinalHash);
    });

    public static string ReadHello(BinaryReader r) => r.ReadString();
    public static WelcomeMsg ReadWelcome(BinaryReader r) => new(r.ReadByte(), r.ReadByte(), r.ReadUInt32(), r.ReadString());
    public static LobbyMsg ReadLobby(BinaryReader r) => new(r.ReadByte(), r.ReadByte());
    public static byte ReadInput(BinaryReader r) => r.ReadByte();
    public static FrameMsg ReadFrame(BinaryReader r, int playerCount) => new(r.ReadInt32(), r.ReadBytes(playerCount));
    public static HashMsg ReadHash(BinaryReader r) => new(r.ReadInt32(), r.ReadUInt64());
    public static DesyncMsg ReadDesync(BinaryReader r) => new(r.ReadInt32(), r.ReadByte());
    public static GameOverMsg ReadGameOver(BinaryReader r) => new(r.ReadByte(), r.ReadSByte(), r.ReadInt32(), r.ReadUInt64());
}
