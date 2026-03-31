using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MockServerA;
using LighthousePrintServer.Protos;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddGrpc(o => o.MaxReceiveMessageSize = 64 * 1024 * 1024);
builder.Services.AddGrpcReflection();
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(k =>
    k.ListenAnyIP(6060, o => o.Protocols = HttpProtocols.Http2));

var app = builder.Build();
app.MapGrpcService<HubService>();
app.MapGrpcReflectionService();

var hostTask = app.RunAsync();
Console.WriteLine("MockServerA listening on :6060\n");

// ── server selection loop ────────────────────────────────────────────────────
while (true)
{
    var machineId = PickServer();
    if (machineId == null) break;
    await ServerMenu(machineId);
}

Console.WriteLine("Shutting down...");
await app.StopAsync();
await hostTask;

// ── helpers ──────────────────────────────────────────────────────────────────

static string? PickServer()
{
    while (true)
    {
        var list = HubService.Clients.Values.ToList();

        if (list.Count == 0)
        {
            Console.WriteLine("No servers connected. Press Enter to refresh, Q to quit.");
            var k = Console.ReadLine();
            if (k?.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) == true) return null;
            continue;
        }

        Console.WriteLine("=== Connected Servers ===");
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            Console.WriteLine($"  {i + 1}. {s.Hostname}  [{s.MachineId}]  {s.Printers.Count} printer(s)  hb {s.LastHeartbeat:HH:mm:ss}");
        }
        Console.WriteLine("  0. Exit");
        Console.Write("> ");

        var input = Console.ReadLine()?.Trim();
        if (input == "0") return null;
        if (int.TryParse(input, out var idx) && idx >= 1 && idx <= list.Count)
            return list[idx - 1].MachineId;

        Console.WriteLine("Invalid.");
    }
}

static async Task ServerMenu(string machineId)
{
    while (true)
    {
        var server = HubService.Clients.GetValueOrDefault(machineId);
        if (server == null) { Console.WriteLine("Server disconnected."); return; }

        Console.WriteLine();
        Console.WriteLine($"=== {server.Hostname} ===");
        Console.WriteLine("  1. Get printers");
        Console.WriteLine("  2. Get print queue");
        Console.WriteLine("  3. Pause printer");
        Console.WriteLine("  4. Resume printer");
        Console.WriteLine("  5. Clear print queue");
        Console.WriteLine("  6. Add document");
        Console.WriteLine("  7. Remove document");
        Console.WriteLine("  0. Back");
        Console.Write("> ");

        switch (Console.ReadLine()?.Trim())
        {
            case "0": return;

            case "1":
                await RunCommand(machineId, HubCommandType.HubCommandGetPrinters);
                break;

            case "2":
                Console.Write("Printer ID: ");
                await RunCommand(machineId, HubCommandType.HubCommandGetQueue,
                    printerId: Console.ReadLine()?.Trim() ?? "");
                break;

            case "3":
                Console.Write("Printer ID: ");
                await RunCommand(machineId, HubCommandType.HubCommandPausePrinter,
                    printerId: Console.ReadLine()?.Trim() ?? "");
                break;

            case "4":
                Console.Write("Printer ID: ");
                await RunCommand(machineId, HubCommandType.HubCommandResumePrinter,
                    printerId: Console.ReadLine()?.Trim() ?? "");
                break;

            case "5":
                Console.Write("Printer ID: ");
                await RunCommand(machineId, HubCommandType.HubCommandClearQueue,
                    printerId: Console.ReadLine()?.Trim() ?? "");
                break;

            case "6":
                Console.Write("Printer ID: ");      var pid  = Console.ReadLine()?.Trim() ?? "";
                Console.Write("File path: ");        var path = Console.ReadLine()?.Trim() ?? "";
                Console.Write("Content type [application/pdf]: "); var ct = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(ct)) ct = "application/pdf";
                Console.Write("Copies [1]: ");
                var copies = int.TryParse(Console.ReadLine()?.Trim(), out var c) ? c : 1;
                await RunAddDocument(machineId, pid, path, ct, copies);
                break;

            case "7":
                Console.Write("Printer ID: ");
                var pid7 = Console.ReadLine()?.Trim() ?? "";
                Console.Write("Job ID: ");
                var jid = Console.ReadLine()?.Trim() ?? "";
                await RunCommand(machineId, HubCommandType.HubCommandRemoveDocument,
                    printerId: pid7, jobId: jid);
                break;

            default:
                Console.WriteLine("Unknown option.");
                break;
        }
    }
}

static async Task RunCommand(string machineId, HubCommandType type,
    string printerId = "", string jobId = "")
{
    try
    {
        var resp = await HubService.SendCommandAsync(machineId, type, printerId, jobId);
        PrintResponse(resp);
    }
    catch (OperationCanceledException) { Console.WriteLine("Timed out."); }
    catch (Exception ex)               { Console.WriteLine($"Error: {ex.Message}"); }
}

static async Task RunAddDocument(string machineId, string printerId,
    string filePath, string contentType, int copies)
{
    try
    {
        if (!File.Exists(filePath)) { Console.WriteLine("File not found."); return; }
        var data = await File.ReadAllBytesAsync(filePath);
        var resp = await HubService.SendAddDocumentAsync(
            machineId, printerId, Path.GetFileName(filePath), data, contentType, copies);
        PrintResponse(resp);
    }
    catch (OperationCanceledException) { Console.WriteLine("Timed out."); }
    catch (Exception ex)               { Console.WriteLine($"Error: {ex.Message}"); }
}

static void PrintResponse(LighthousePrintServer.Protos.CommandResponse resp)
{
    if (!resp.Success) { Console.WriteLine($"Error: {resp.Error}"); return; }
    if (resp.Payload.IsEmpty) { Console.WriteLine("OK"); return; }

    var json = resp.Payload.ToStringUtf8();
    try
    {
        var obj = JsonSerializer.Deserialize<object>(json);
        Console.WriteLine(JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
    }
    catch { Console.WriteLine(json); }
}
