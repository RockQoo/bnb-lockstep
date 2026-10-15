using BnB.Server;

// 사용법: BnB.Server [--port 7777] [--players 2] [--seed N] [--replays 경로] [--games N]
var options = ServerOptions.Parse(args);
var server = new GameServer(options);
server.Run();
