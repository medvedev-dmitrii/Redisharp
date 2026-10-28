# RedisSharp

[![CI](https://github.com/YOUR_USERNAME/RedisSharp/actions/workflows/ci.yml/badge.svg)](https://github.com/YOUR_USERNAME/RedisSharp/actions/workflows/ci.yml)
![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet)
![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)

A small, fast, in-memory key-value server written from scratch in C# that speaks the
**Redis wire protocol (RESP2)**. Point `redis-cli` — or any Redis client library — at it and it just works.

```
$ redis-cli -p 6379
127.0.0.1:6379> SET greeting "hello from .NET" EX 60
OK
127.0.0.1:6379> GET greeting
"hello from .NET"
127.0.0.1:6379> TTL greeting
(integer) 58
127.0.0.1:6379> INCR visits
(integer) 1
```

This is **not** a Redis replacement. It's a learning project that explores how a network
database is put together: binary protocol parsing, zero-copy I/O with `System.IO.Pipelines`,
lock-free concurrent data structures, and key expiry strategies — all with a thorough test suite.

## Features

- **RESP2 protocol** — incremental parser over `ReadOnlySequence<byte>`, handles fragmented
  TCP reads and pipelined requests; serialiser writes straight into an `IBufferWriter<byte>`.
- **Inline commands** — `telnet`/`nc` friendly (`SET a 1` without the array framing).
- **Async TCP server** — `Socket.AcceptAsync` + `System.IO.Pipelines`, one task per connection.
- **Thread-safe store** — `ConcurrentDictionary` with compare-and-swap loops; `INCR` is atomic
  under contention (there's a test that hammers it with 16 threads).
- **Key expiry** — `EX`/`PX`/`NX`/`XX` options on `SET`, plus `EXPIRE`, `PEXPIRE`, `TTL`,
  `PTTL`, `PERSIST`. Expired keys are reaped both lazily on access and by a background sweeper,
  the same two-pronged approach Redis uses.
- **Glob matching** for `KEYS` (`*`, `?`, `[abc]`, escapes).
- **Testable time** — `IClock` abstraction means TTL tests advance a fake clock instead of sleeping.
- **26 commands**, each a tiny class implementing `ICommand`; adding a new one is a 10-line change.
- **Generic Host** integration — configuration via `appsettings.json` / env vars, structured logging.

## Supported commands

| Group | Commands |
|---|---|
| Connection | `PING`, `ECHO`, `COMMAND` |
| Server | `INFO`, `DBSIZE`, `FLUSHDB` |
| Strings | `GET`, `SET [EX s] [PX ms] [NX\|XX]`, `SETNX`, `GETSET`, `MGET`, `MSET`, `APPEND`, `STRLEN` |
| Counters | `INCR`, `DECR`, `INCRBY`, `DECRBY` |
| Keys | `DEL`, `EXISTS`, `KEYS pattern` |
| Expiry | `EXPIRE`, `PEXPIRE`, `TTL`, `PTTL`, `PERSIST` |

## Getting started

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download).

```bash
git clone https://github.com/YOUR_USERNAME/RedisSharp.git
cd RedisSharp
dotnet run --project src/RedisSharp.Server
```

Override the port or bind address from the environment:

```bash
Server__Port=7000 Server__Host=0.0.0.0 dotnet run --project src/RedisSharp.Server
```

Talk to it with `redis-cli`, or with nothing but netcat:

```bash
printf 'SET answer 42\r\nGET answer\r\n' | nc localhost 6379
+OK
$2
42
```

## Running the tests

```bash
dotnet test
```

The suite covers three layers:

- **`RespParserTests`** — every RESP type, nested arrays, partial frames, multi-segment buffers,
  malformed input, writer ⇄ parser round-trips.
- **`KeyValueStoreTests`** / **`CommandTests`** — store semantics and command behaviour against a
  fake clock, so expiry is tested deterministically.
- **`ServerIntegrationTests`** — boots the real server on a random port and talks raw RESP over a
  `TcpClient`: pipelining, split frames, multiple clients, error recovery.

## Architecture

```
┌──────────────────────┐        ┌─────────────────────────────────────────┐
│  RedisSharp.Server   │        │           RedisSharp.Core               │
│                      │        │                                         │
│  RedisServer ────────┼──────▶ │  Protocol/   RespParser, RespWriter     │
│   (accept loop)      │        │              RespValue hierarchy        │
│      │               │        │                                         │
│  ClientConnection ───┼──────▶ │  Commands/   CommandDispatcher          │
│   (PipeReader/Writer)│        │              ICommand + 26 handlers     │
│      │               │        │                                         │
│  ExpirySweeper ──────┼──────▶ │  Storage/    KeyValueStore (TTL, CAS)   │
│   (PeriodicTimer)    │        │              GlobMatcher, IClock         │
└──────────────────────┘        └─────────────────────────────────────────┘
```

`RedisSharp.Core` has **zero dependencies** beyond the BCL and knows nothing about sockets,
which is what makes the command and store tests trivial to write.

### Request flow

1. `ClientConnection` reads bytes into a `PipeReader`.
2. `RespParser.TryParse` is called in a loop; it either yields a complete `RespValue` (and the
   position to advance to) or returns `false` so we wait for more bytes. Nothing is copied until a
   whole frame has arrived.
3. `CommandDispatcher` validates arity, finds the `ICommand`, and turns any exception into a
   `-ERR` reply so a bad request never drops the connection.
4. `RespWriter` serialises the reply into the `PipeWriter`; one flush per read batch, so pipelined
   requests get pipelined replies.

### Adding a command

```csharp
public sealed class HelloCommand : ICommand
{
    public string Name => "HELLO";
    public int Arity => 2; // HELLO <name>

    public RespValue Execute(CommandContext ctx, IReadOnlyList<string> args) =>
        RespValue.Bulk($"Hi, {args[1]}!");
}
```

Register it in `BuiltInCommands.All()` and it's live. Arity follows the Redis convention:
positive = exact count, negative = minimum.

## Roadmap / ideas

- [ ] Hashes, lists and sets (`HSET`, `LPUSH`, `SADD`, …)
- [ ] `SCAN` cursor iteration
- [ ] `MULTI`/`EXEC` transactions
- [ ] Append-only-file persistence

