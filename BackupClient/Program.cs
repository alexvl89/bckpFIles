using BackupServer;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace BackupClient
{
    class Program
    {
        static async Task Main(string[] args)
        {
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });
            var logger = loggerFactory.CreateLogger<Program>();

            try
            {
                AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };

                using var channel = GrpcChannel.ForAddress("https://192.168.80.145:5001", new GrpcChannelOptions
                {
                    HttpHandler = handler
                });

                var client = new Backup.BackupClient(channel);
                logger.LogInformation("Requesting backup from server...");

                // Тест Ping
                logger.LogInformation("Тестируем Ping");
                var pingResponse = await client.PingAsync(new PingRequest());
                logger.LogInformation($"Ответ Ping: {pingResponse.Message}");

                // Тест CheckHealth
                Console.WriteLine("\nTesting CheckHealth...");
                var healthResponse = await client.CheckHealthAsync(new HealthRequest());
                Console.WriteLine($"Health Response: {healthResponse.Status}");

                // Вызов gRPC метода с потоковым ответом
                using var call = client.GetBackup(new BackupRequest { BackupId = "backup_123" });

                var filePath = "received_backup.db";
                long totalBytesReceived = 0;

                using var fileStream = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1 * 1024 * 1024,
                    useAsync: true);

                // Чтение потока данных
                await foreach (var response in call.ResponseStream.ReadAllAsync())
                {
                    if (response.Status != null)
                    {
                        // Вывод информации о состоянии
                        logger.LogInformation("Backup status: {StatusMessage}, Progress: {Progress}%",
                            response.Status.StatusMessage, response.Status.Progress);
                    }
                    else if (response.Data.Length > 0)
                    {
                        // Запись данных в файл
                        await fileStream.WriteAsync(response.Data.ToByteArray());
                        totalBytesReceived += response.Data.Length;

                        // Логирование прогресса каждые ~10 ГБ
                        const long TenGigabytes = 10L * 1024 * 1024 * 1024;
                        if (totalBytesReceived % TenGigabytes < response.Data.Length)
                        {
                            logger.LogInformation("Backup transfer progress: {Bytes:F2} GB received",
                                totalBytesReceived / (1024.0 * 1024.0 * 1024.0));
                        }
                    }
                }

                await fileStream.FlushAsync();
                logger.LogInformation("Backup successfully received and saved to {FilePath}. Total size: {Bytes:F2} GB",
                    filePath, totalBytesReceived / (1024.0 * 1024.0 * 1024.0));
            }
            catch (RpcException ex)
            {
                logger.LogError("gRPC error: {StatusCode} - {Detail}", ex.StatusCode, ex.Status.Detail);
            }
            catch (IOException ex)
            {
                logger.LogError(ex, "IO error while saving backup file");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error during backup");
            }
        }
    }
}