using BnB.Net;

namespace BnB.Server;

public sealed class ServerOptions
{
    public int Port { get; private set; } = Protocol.DefaultPort;
    public int Players { get; private set; } = 2;
    public uint? Seed { get; private set; }
    public string ReplayDir { get; private set; } = "replays";
    public int Games { get; private set; }  // 0 = 무한
    public string MapId { get; private set; } = "pirate14";

    public static ServerOptions Parse(string[] args)
    {
        var o = new ServerOptions();
        for (int i = 0; i < args.Length; ++i)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--port": o.Port = int.Parse(Next()); break;
                case "--players": o.Players = int.Parse(Next()); break;
                case "--seed": o.Seed = uint.Parse(Next()); break;
                case "--replays": o.ReplayDir = Next(); break;
                case "--games": o.Games = int.Parse(Next()); break;
                case "--map": o.MapId = Next(); break;
                default: throw new ArgumentException($"unknown option {args[i]}");
            }
        }
        if (o.Players is < 1 or > 4)
            throw new ArgumentException("--players must be 1..4");
        return o;
    }
}
