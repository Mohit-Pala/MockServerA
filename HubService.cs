using System.Collections.Concurrent;
using System.Text.Json;
using Grpc.Core;
using LighthousePrintServer.Protos;

namespace MockServerA;

internal class ConnectedServer
{
    public required string MachineId { get; init; }
    public required string Hostname { get; init; }
    public required IReadOnlyList<PrinterInfo> Printers { get; init; }
    public DateTime ConnectedAt { get; init; } = DateTime.UtcNow;
    public DateTime LastHeartbeat { get; set; } = DateTime.UtcNow;
    internal required IServerStreamWriter<HubMessage> Writer { get; init; }
    internal ConcurrentDictionary<string, TaskCompletionSource<CommandResponse>> Pending { get; } = new();
}

public class HubService : PrintServerHub.PrintServerHubBase
{
    internal static readonly ConcurrentDictionary<string, ConnectedServer> Clients = new();

    public override async Task Connect(
        IAsyncStreamReader<PrintServerMessage> requestStream,
        IServerStreamWriter<HubMessage> responseStream,
        ServerCallContext context)
    {
        ConnectedServer? server = null;

        await foreach (var msg in requestStream.ReadAllAsync(context.CancellationToken))
        {
            switch (msg.PayloadCase)
            {
                case PrintServerMessage.PayloadOneofCase.Register:
                    var reg = msg.Register;
                    server = new ConnectedServer
                    {
                        MachineId  = reg.MachineId,
                        Hostname   = reg.Hostname,
                        Printers   = reg.Printers.ToList(),
                        Writer     = responseStream
                    };
                    Clients[reg.MachineId] = server;

                    Console.WriteLine($"\n[+] {reg.Hostname} ({reg.MachineId}) — {reg.Printers.Count} printer(s)");
                    foreach (var p in reg.Printers)
                        Console.WriteLine($"    • {p.PrinterId}  {p.DisplayName}{(p.IsDefault ? " [default]" : "")}{(p.IsPaused ? " [paused]" : "")}");
                    Console.Write("> ");

                    await responseStream.WriteAsync(new HubMessage
                    {
                        RegRes = new RegisterResponse { Accepted = true, Message = "Connected to MockServerA" }
                    });
                    break;

                case PrintServerMessage.PayloadOneofCase.Heartbeat:
                    if (server != null) server.LastHeartbeat = DateTime.UtcNow;
                    break;

                case PrintServerMessage.PayloadOneofCase.Response:
                    if (server != null && server.Pending.TryRemove(msg.Response.CommandId, out var tcs))
                        tcs.SetResult(msg.Response);
                    break;
            }
        }

        if (server != null)
        {
            Clients.TryRemove(server.MachineId, out _);
            Console.WriteLine($"\n[-] {server.Hostname} disconnected");
            Console.Write("> ");
        }
    }

    // Send a simple command (no extra fields) and wait for response
    internal static async Task<CommandResponse> SendCommandAsync(
        string machineId,
        HubCommandType type,
        string printerId = "",
        string jobId = "",
        CancellationToken ct = default)
    {
        if (!Clients.TryGetValue(machineId, out var server))
            throw new InvalidOperationException($"'{machineId}' is not connected.");

        var commandId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Pending[commandId] = tcs;

        await server.Writer.WriteAsync(new HubMessage
        {
            Command = new HubCommand
            {
                CommandId = commandId,
                Type      = type,
                PrinterId = printerId,
                JobId     = jobId
            }
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }

    // AddDocument — separate method because it carries a file payload
    internal static async Task<CommandResponse> SendAddDocumentAsync(
        string machineId,
        string printerId,
        string documentName,
        byte[] data,
        string contentType,
        int copies,
        CancellationToken ct = default)
    {
        if (!Clients.TryGetValue(machineId, out var server))
            throw new InvalidOperationException($"'{machineId}' is not connected.");

        var commandId = Guid.NewGuid().ToString("N")[..8];
        var tcs = new TaskCompletionSource<CommandResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Pending[commandId] = tcs;

        await server.Writer.WriteAsync(new HubMessage
        {
            Command = new HubCommand
            {
                CommandId    = commandId,
                Type         = HubCommandType.HubCommandAddDocument,
                PrinterId    = printerId,
                DocumentName = documentName,
                DocumentData = Google.Protobuf.ByteString.CopyFrom(data),
                ContentType  = contentType,
                Copies       = copies
            }
        }, ct);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(120));
        cts.Token.Register(() => tcs.TrySetCanceled());
        return await tcs.Task;
    }
}
