using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Ipc;

internal sealed partial class CaptureIpcServer
{
    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);

                NamedPipeServerStream connection = server;
                server = null;
                Task handler = Task.Run(() => ServeConnectionAsync(connection), CancellationToken.None);
                lock (_connections)
                {
                    _connections.RemoveAll(task => task.IsCompleted);
                    _connections.Add(handler);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException ex)
            {
                LastError = ex.Message;
                await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    private async Task ServeConnectionAsync(NamedPipeServerStream connection)
    {
        using (connection)
        {
            using StreamReader reader = new(connection, Encoding.UTF8, false, 4096, leaveOpen: true);
            using StreamWriter writer = new(connection, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException)
                {
                    return;
                }

                if (line is null)
                {
                    return;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                IpcResponse response;
                try
                {
                    IpcRequest? request = JsonSerializer.Deserialize<IpcRequest>(line, RecallJson.WireOptions);
                    response = request is null
                        ? IpcResponse.Failure(0, "empty request")
                        : Handle(request);
                }
                catch (JsonException ex)
                {
                    response = IpcResponse.Failure(0, $"malformed request: {ex.Message}");
                }

                RequestsHandled++;
                try
                {
                    // One line per response: the framing is line based, so the wire options must not
                    // indent (see RecallJson.WireOptions).
                    await writer.WriteLineAsync(JsonSerializer.Serialize(response, RecallJson.WireOptions))
                        .ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    LastError = ex.Message;
                    return;
                }
            }
        }
    }
}
