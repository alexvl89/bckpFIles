using BackupServer;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

namespace BackupClient;

class Program
{
    static async Task Main(string[] args)
    {
        // Настройка логирования
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConsole();
            builder.SetMinimumLevel(LogLevel.Information);
        });
        var logger = loggerFactory.CreateLogger<Program>();

        try
        {
            // Настройка HTTP клиента
            var handler = new HttpClientHandler
            {
                // Только для разработки! В продакшене использовать валидный сертификат
                ServerCertificateCustomValidationCallback =
                    HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
            };

            // Создание gRPC канала
            using var channel = GrpcChannel.ForAddress("http://localhost:5000", new GrpcChannelOptions
            {
                HttpHandler = handler
            });

            // Исправьте строку создания клиента gRPC.
            // Вместо BackupServer.BackupClient используйте правильный тип клиента, который был сгенерирован из вашего .proto-файла.
            // Обычно он называется <ServiceName>Client, где <ServiceName> — это имя сервиса в .proto.
            // Например, если ваш сервис называется Backup, используйте Backup.BackupClient.

            var client = new Backup.BackupClient(channel);

            logger.LogInformation("Requesting backup from server...");

            // Вызов gRPC метода с потоковым ответом
            using var call = client.GetBackup(new BackupRequest());
            var filePath = "received_backup.db";
            long totalBytesReceived = 0;

            // Открываем файл для записи
            using var fileStream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1 * 1024 * 1024, // 1 МБ буфер
                useAsync: true);

            // Чтение потока данных
            await foreach (var response in call.ResponseStream.ReadAllAsync())
            {
                if (response.Data.Length > 0)
                {
                    // Записываем полученные данные в файл
                    await fileStream.WriteAsync(response.Data.ToByteArray());
                    totalBytesReceived += response.Data.Length;

                    // Логируем прогресс каждые ~10 ГБ
                    const long TenGigabytes = 10L * 1024 * 1024 * 1024; // 10 ГБ в байтах
                    if (totalBytesReceived % TenGigabytes < response.Data.Length)
                    {
                        logger.LogInformation("Backup transfer progress: {Bytes:F2} GB received",
                            FormatFileSize(totalBytesReceived));
                            /// (1024.0 * 1024.0 * 1024.0));
                    }
                }
            }

            await fileStream.FlushAsync();
            logger.LogInformation("Backup successfully received and saved to {FilePath}. Total size: {Bytes:F2}",
                filePath,
                FormatFileSize(totalBytesReceived));
            //totalBytesReceived / (1024.0 * 1024.0 * 1024.0));
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

    // Форматирование размера файла с динамической единицей измерения
    private static string FormatFileSize(long bytes)
    {
        const long KB = 1024;
        const long MB = 1024 * KB;
        const long GB = 1024 * MB;

        if (bytes >= GB)
            return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
        else if (bytes >= MB)
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        else if (bytes >= KB)
            return $"{bytes / 1024.0:F2} KB";
        else
            return $"{bytes:F0} bytes";
    }
}