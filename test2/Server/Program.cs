using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using System.IO; // Add this to use TextWriter
using Microsoft.Extensions.Logging.Abstractions;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
namespace WebSocketServer
{
    public class Program
    {
        // A thread-safe collection to store connected clients and their subscriptions
        private static readonly ConcurrentDictionary<WebSocket, ConcurrentBag<string>> clientSubscriptions = new ConcurrentDictionary<WebSocket, ConcurrentBag<string>>();
        private static ILogger<Program>? _logger;  // Make _logger nullable to avoid warnings

        public static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsoleFormatter<CustomConsoleFormatter, ConsoleFormatterOptions>(); // Use custom formatter
                builder.AddConsole(options =>
                {
                    options.FormatterName = "customFormatter"; // Set custom formatter name
                });
            });

            _logger = loggerFactory.CreateLogger<Program>();

            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                _logger?.LogInformation("Server shutting down...");
                foreach (var client in clientSubscriptions.Keys)
                {
                    client.Abort();
                }
                Environment.Exit(0);
            };

            var listener = new HttpListener();
            listener.Prefixes.Add("http://localhost:5000/");
            listener.Start();

            _logger?.LogInformation("Server started at ws://localhost:5000/");

            // Start the background services
            _ = Task.Run(() => StartTimeService());
            _ = Task.Run(() => StartCpuService());

            while (true)
            {
                var context = await listener.GetContextAsync();
                if (context.Request.IsWebSocketRequest)
                {
                    var wsContext = await context.AcceptWebSocketAsync(null);
                    HandleConnection(wsContext.WebSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }

        static void HandleConnection(WebSocket webSocket)
        {
            clientSubscriptions.TryAdd(webSocket, new ConcurrentBag<string>()); // Add new client with empty subscriptions

            Task.Run(async () =>
            {
                var buffer = new byte[1024 * 4];
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                while (!result.CloseStatus.HasValue)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger?.LogInformation($"Received: {message}");

                    // Process subscription requests
                    if (message.StartsWith("SUBSCRIBE:"))
                    {
                        var topic = message.Split(':')[1].Trim();
                        if (!string.IsNullOrEmpty(topic))
                        {
                            clientSubscriptions[webSocket].Add(topic);
                            _logger?.LogWarning($">>> Client subscribed to topic '{topic}'");
                        }
                    }

                    result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                }

                clientSubscriptions.TryRemove(webSocket, out _); // Remove client when closed
                await webSocket.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
                _logger?.LogInformation("Connection closed.");
            });
        }

        static async Task StartTimeService()
        {
            while (true)
            {
                string message = $"TIME: {DateTime.UtcNow:O}";
                await BroadcastMessage(message, "TIME");
                _logger?.LogInformation(message);
                await Task.Delay(10000);
            }
        }

        static async Task StartCpuService()
        {
            var cpuUsage = new PerformanceCounter("Processor", "% Processor Time", "_Total");

            while (true)
            {
                cpuUsage.NextValue(); // The first call usually returns 0, so we need a delay.
                await Task.Delay(1000);
                float usage = cpuUsage.NextValue();
                string message = $"CPU: {usage}%";
                await BroadcastMessage(message);
                _logger?.LogInformation(message);
                await Task.Delay(5000);
            }
        }

        static async Task BroadcastMessage(string message, string topic = null)
        {
            var buffer = Encoding.UTF8.GetBytes(message);
            var tasks = new List<Task>();

            _logger?.LogWarning($">>> Broadcasting topic '{topic}'");
            foreach (var client in clientSubscriptions.Keys)
            {
                if (client.State == WebSocketState.Open)
                {
                    _logger?.LogWarning($">>> Sending to client message for topic '{topic}'");
                    tasks.Add(client.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, CancellationToken.None));
                }
            }

            await Task.WhenAll(tasks);
        }
    }
}

// Custom Console Formatter class
public class CustomConsoleFormatter : ConsoleFormatter
{
    public CustomConsoleFormatter() : base("customFormatter") { }

    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        // Save the current console color
        var originalColor = Console.ForegroundColor;

        // Determine the color and text for the log level
        ConsoleColor logLevelColor = originalColor; // Default to the original color
        string logLevelText = logEntry.LogLevel.ToString(); // Default log level text
        switch (logEntry.LogLevel)
        {
            case LogLevel.Information:
                logLevelColor = ConsoleColor.Green;
                logLevelText = "Info"; // Change 'Information' to 'Info'
                break;
            case LogLevel.Warning:
                logLevelColor = ConsoleColor.Yellow;
                logLevelText = "Warn"; // Change 'Warning' to 'Warn'
                break;
            case LogLevel.Error:
            case LogLevel.Critical:
                logLevelColor = ConsoleColor.Red;
                break;
            case LogLevel.Debug:
                logLevelColor = ConsoleColor.Blue;
                break;
            case LogLevel.Trace:
                logLevelColor = ConsoleColor.Cyan;
                break;
            default:
                break;
        }

        // Custom log message formatting
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);

        // Ensure the message is not null or empty before writing to the console
        if (!string.IsNullOrEmpty(message))
        {
            // Write the timestamp
            Console.Out.Write($"{timestamp} ");

            // Write the log level with color
            Console.ForegroundColor = logLevelColor; // Set color for log level
            Console.Out.Write($"{logLevelText}: ");
            Console.ForegroundColor = originalColor; // Reset color for the rest of the message

            // Write the rest of the message
            Console.Out.WriteLine($"{message}");
        }

        // Restore the original console color
        Console.ForegroundColor = originalColor;
    }
}