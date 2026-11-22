# gRPC Greeter — Learning Demo

> ⚠️ **This is a learning project, not a production application.**
>
> It exists to understand what gRPC is, how it works, and how the pieces are
> wired together. The code is deliberately small and is missing everything a
> real service needs (auth, config, retries, tests, and so on).

A minimal **gRPC server + client** in .NET 10:

- **Server** — an ASP.NET Core app that implements a `Greeter` service.
- **Client** — a console app that sends a list of names, one RPC call per name,
  and prints the greeting plus the **binary**, **JSON**, and **size** of every
  request and reply.

---

## Table of contents

- [What is gRPC?](#what-is-grpc)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Prerequisites](#prerequisites)
- [Build and run](#build-and-run)
- [What the client prints](#what-the-client-prints)
- [Wiring a new RPC](#wiring-a-new-rpc)
- [Troubleshooting](#troubleshooting)
- [Things to try](#things-to-try)
- [Not production-ready](#not-production-ready)

---

## What is gRPC?

gRPC is a **remote procedure call (RPC)** framework. You describe a service and
its messages in a `.proto` file, and the tooling generates strongly typed client
and server code. Calls are sent as **binary Protocol Buffers over HTTP/2**, so
the client looks like it is calling a local method.

| | gRPC | REST / JSON |
|---|---|---|
| Payload | binary (protobuf) | text (JSON) |
| Contract | `.proto`, strongly typed | OpenAPI / docs (optional) |
| Transport | HTTP/2 (supports streaming) | usually HTTP/1.1 |
| Code generation | built in | extra tooling |
| Browser support | needs gRPC-Web | native |

---

## How it works

### 1. The contract

Everything starts with `Protos/greet.proto`:

```proto
syntax = "proto3";

package greet;                        // becomes the URL path prefix

service Greeter {                     // → a C# class named Greeter
  rpc SayHello (HelloRequest) returns (HelloReply);
}

message HelloRequest { string name = 1; }     // → C# class HelloRequest
message HelloReply   { string message = 1; }  // → C# class HelloReply
```

`= 1` is a **field number**, not a default value. It is the tag written on the
wire (for example `0A` means "field 1, wire type 2").

### 2. Code generation

Each project references the proto file in its `.csproj`:

```xml
<!-- server -->
<Protobuf Include="Protos\greet.proto" GrpcServices="Server" />

<!-- client -->
<Protobuf Include="Protos\greet.proto" GrpcServices="Client" />
```

At build time `Grpc.Tools` runs `protoc` and writes generated C# into
`obj/Debug/net10.0/Protos/`:

| Generated file | Contains |
|---|---|
| `Greet.cs` | `HelloRequest`, `HelloReply` (serialization code) |
| `GreetGrpc.cs` | `Greeter.GreeterBase` (server) and/or `Greeter.GreeterClient` (client) |

```
service Greeter  →  public static partial class Greeter
rpc SayHello     →  GreeterBase.SayHello(...)          [server]
                    GreeterClient.SayHelloAsync(...)   [client]
```

That is why `GreeterService : Greeter.GreeterBase` compiles — the base class is
generated.

### 3. Server wiring

`GrpcGreeter/Program.cs`:

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddGrpc();                 // register gRPC in DI

var app = builder.Build();

app.MapGrpcService<GreeterService>();       // expose the service
app.Run();
```

`GrpcGreeter/Services/GreeterService.cs`:

```csharp
public class GreeterService(ILogger<GreeterService> logger) : Greeter.GreeterBase
{
    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
        => Task.FromResult(new HelloReply { Message = "Hello " + request.Name });
}
```

You override the generated virtual method — no controllers, no route attributes.

The server must listen on an **HTTP/2** endpoint (`Properties/launchSettings.json`):

```json
"https": { "applicationUrl": "https://localhost:7003;http://localhost:5108" }
```

On `https://`, Kestrel negotiates HTTP/2 automatically via TLS/ALPN. A plain
`http://` endpoint defaults to HTTP/1.1 and cannot serve gRPC without extra
h2c configuration.

### 4. Client wiring

`GrpcGreeterClient/Program.cs`:

```csharp
using var channel = GrpcChannel.ForAddress("https://localhost:7003"); // 1. connection
var client = new Greeter.GreeterClient(channel);                      // 2. generated stub

var reply = await client.SayHelloAsync(new HelloRequest { Name = "Ahmed" }); // 3. call
```

- `GrpcChannel` is a long-lived HTTP/2 connection — create it once and reuse it.
- `GreeterClient` is the generated proxy; `SayHelloAsync` is a normal `await`-able
  method.
- The address **must match** the address the server listens on.

### 5. On the wire

```
Client                                      Server (Kestrel)
  │  HelloRequest { Name = "Ahmed" }          │
  │  serialize → 0A 05 41 68 6D 65 64         │
  │                                           │
  │  POST /greet.Greeter/SayHello             │
  │  content-type: application/grpc           │
  │  te: trailers                             │
  │  body: [00][00 00 00 07][7 bytes]         │  ← 5-byte gRPC frame header
  │ ────────────────────────────────────────▶ │
  │                                           │  deserialize → SayHello()
  │                                           │  serialize reply
  │  ◀──────────────────────────────────────── │  headers + body + trailers
  │  grpc-status: 0                           │
  │  reply → HelloReply { Message = "Hello Ahmed" }
```

- **URL path** = `/{package}.{service}/{method}` → `/greet.Greeter/SayHello`.
- **Body** = 1-byte compression flag (`00`) + 4-byte big-endian length + the
  protobuf bytes.
- **Status** travels in HTTP/2 **trailers** (`grpc-status`), not the HTTP status
  code. That is why failures throw `RpcException` with a gRPC status such as
  `Unavailable` instead of an HTTP error.

---

## Project structure

```
E:\gRPC\                              repo root
├── README.md
├── .gitignore
└── GrpcGreeter\                      project folder
    ├── GrpcGreeter\                  server (ASP.NET Core)
    │   ├── Protos\greet.proto        contract
    │   ├── Services\GreeterService.cs
    │   ├── Program.cs
    │   ├── Properties\launchSettings.json
    │   ├── appsettings.json
    │   └── GrpcGreeter.csproj
    └── GrpcGreeterClient\            client (console)
        ├── Protos\greet.proto        same contract (duplicated)
        ├── Program.cs                loops over names + dumps bytes
        └── GrpcGreeterClient.csproj
```

> The `.proto` file exists in both projects. For a real project, move it to a
> shared contracts library so it has a single source of truth.

---

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A trusted ASP.NET Core development certificate:

  ```powershell
  dotnet dev-certs https --trust
  ```

All commands below are run from the repository root.

---

## Build and run

Build both projects:

```powershell
dotnet build GrpcGreeter/GrpcGreeter/GrpcGreeter.csproj
dotnet build GrpcGreeter/GrpcGreeterClient/GrpcGreeterClient.csproj
```

Start the server (terminal 1) — the `https` profile is required so it listens on
port 7003:

```powershell
dotnet run --project GrpcGreeter/GrpcGreeter --launch-profile https
# wait for: Now listening on: https://localhost:7003
```

Run the client (terminal 2):

```powershell
dotnet run --project GrpcGreeter/GrpcGreeterClient
```

> `dotnet run` uses the **first** profile in `launchSettings.json`, which is
> `http` (port 5108). Without `--launch-profile https` the server will not be on
> 7003 and the client will fail with `StatusCode="Unavailable"`.

---

## What the client prints

For each name the client prints the request, the reply, and both encodings with
their sizes:

```
--- Request 1 ---
Binary :   7 bytes | hex: 0A0541686D6564
         bits: 00001010 00000101 01000001 01101000 01101101 01100101 01100100
JSON   :  19 bytes | { "name": "Ahmed" }
Protobuf is 12 bytes smaller
--- Reply 1 ---
Binary :  13 bytes | hex: 0A0B48656C6C6F2041686D6564
JSON   :  28 bytes | { "message": "Hello Ahmed" }
Protobuf is 15 bytes smaller
Greeting 1: Hello Ahmed
```

Reading the bytes for `HelloRequest { Name = "Ahmed" }`:

```
0A 05 41 68 6D 65 64
│  │  └── "Ahmed" (UTF-8)
│  └───── length = 5
└──────── tag: field 1, wire type 2 (length-delimited)
```

| Name  | Request binary | Request JSON | Reply binary | Reply JSON |
|-------|---------------:|-------------:|-------------:|-----------:|
| Ahmed | 7 B | 19 B | 13 B | 28 B |
| Sara  | 6 B | 18 B | 12 B | 27 B |
| Ali   | 5 B | 17 B | 11 B | 26 B |
| Mona  | 6 B | 18 B | 12 B | 27 B |

Notes:

- The JSON is the **proto3 JSON mapping** (`JsonFormatter`), not what gRPC sends.
  gRPC always sends binary. `JsonFormatter.Default` adds spaces, so the JSON is
  slightly larger than a compact serialization would be.
- These sizes are the **message payload**. Real gRPC adds a 5-byte frame header
  per message plus HTTP/2 headers and trailers.

---

## Wiring a new RPC

1. Add the `rpc` and its request/reply messages to **both** `greet.proto` files.
2. Rebuild both projects so the generated code is updated.
3. Server: `override` the new method in `GreeterService`.
4. Server: no extra registration needed if it is on the same service —
   `MapGrpcService` already covers it.
5. Client: call `client.YourMethodAsync(...)`.
6. Make sure the server is running on the same `https://host:port` the client
   uses, with a trusted certificate.

---

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `Unavailable ... actively refused` | Nothing is listening on the client's address | Start the server; use `--launch-profile https` so it binds port 7003 |
| `CS8802: Only one compilation unit can have top-level statements` | A second project nested inside the server folder got compiled | Keep projects in sibling folders |
| `CS0579: Duplicate 'AssemblyCompanyAttribute'` | Same nested-project problem (generated `obj` files compiled twice) | Keep projects in sibling folders |
| `Cannot read keys when ... input has been redirected` | `Console.ReadKey()` with redirected stdin | Use `Console.ReadLine()` |
| SSL / certificate error | Dev certificate not trusted | `dotnet dev-certs https --trust` |

---

## Things to try

- Change the `names` array in the client.
- Add a field to `HelloRequest` and rebuild both projects.
- Return one reply per name (`repeated string messages`).
- Add a new RPC to `greet.proto`.
- Try a client-streaming or server-streaming RPC.
- Use a shared contracts project to remove the duplicated `.proto`.

---

## Not production-ready

- The server URL is hardcoded in the client.
- No authentication, authorization, retries, deadlines, or error handling.
- `greet.proto` is duplicated in both projects and can drift.
- Logging is minimal and there are no tests.
- Uses the ASP.NET Core development certificate only.

---

## License

This project is for learning purposes. Use it freely.
