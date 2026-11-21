using System.Text;
using Google.Protobuf;
using Grpc.Net.Client;
using GrpcGreeterClient;

using var channel = GrpcChannel.ForAddress("https://localhost:7003");
var client = new Greeter.GreeterClient(channel);

var names = new[] { "Ahmed", "Sara", "Ali", "Mona" };

for (int i = 0; i < names.Length; i++)
{
    var request = new HelloRequest { Name = names[i] };
    Dump($"Request {i + 1}", request);

    var reply = await client.SayHelloAsync(request);
    Dump($"Reply {i + 1}", reply);

    Console.WriteLine($"Greeting {i + 1}: {reply.Message}");
    Console.WriteLine();
}

Console.WriteLine("Press Enter to exit...");
Console.ReadLine();

static void Dump(string label, IMessage message)
{
    var bytes = message.ToByteArray();
    var json = JsonFormatter.Default.Format(message);
    var jsonBytes = Encoding.UTF8.GetByteCount(json);
    var bits = string.Join(" ", bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));

    Console.WriteLine($"--- {label} ---");
    Console.WriteLine($"Binary : {bytes.Length,3} bytes | hex: {Convert.ToHexString(bytes)}");
    Console.WriteLine($"         bits: {bits}");
    Console.WriteLine($"JSON   : {jsonBytes,3} bytes | {json}");
    Console.WriteLine($"Protobuf is {jsonBytes - bytes.Length} bytes smaller");
}
