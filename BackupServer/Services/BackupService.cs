using BackupServer.Services;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackupServer;

// Класс для передачи данных о прогрессе
public class BackupProgressEventArgs : EventArgs
{
    public string StatusMessage { get; }
    public double Progress { get; } // В процентах (0-100)
    public long BytesProcessed { get; }
    public long TotalBytes { get; }

    public BackupProgressEventArgs(string statusMessage, double progress, long bytesProcessed = 0, long totalBytes = 0)
    {
        StatusMessage = statusMessage;
        Progress = progress;
        BytesProcessed = bytesProcessed;
        TotalBytes = totalBytes;
    }
}

public class BackupService : Backup.BackupBase
{
    private readonly ILogger<BackupService> _logger;
    private readonly IBackupServiceLocal backupLocal;
    private const int DefaultChunkSize = 1 * 1024 * 1024; // 1 MB

    // Событие для уведомления о прогрессе
    public delegate Task AsyncEventHandler<TEventArgs>(object sender, TEventArgs e);
    public event AsyncEventHandler<BackupProgressEventArgs> OnProgress;

    // Метод для вызова асинхронного события
    private async Task ReportProgressAsync(string message, double progress, long bytesProcessed = 0, long totalBytes = 0, CancellationToken cancellationToken = default)
    {
        var eventArgs = new BackupProgressEventArgs(message, progress, bytesProcessed, totalBytes);
        var handlers = OnProgress;

        if (handlers != null)
        {
            // Получаем всех подписчиков
            var invocationList = handlers.GetInvocationList();

            // Вызываем каждого подписчика асинхронно
            foreach (var handler in invocationList)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await ((AsyncEventHandler<BackupProgressEventArgs>)handler).Invoke(this, eventArgs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in OnProgress event handler for message: {Message}", message);
                }
            }
        }
    }


    public BackupService(ILogger<BackupService> logger, IBackupServiceLocal backupLocal)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.backupLocal = backupLocal;
    }


    private async Task Sent(string data, IServerStreamWriter<BackupResponse> responseStream)
    {
        var status = new BackupStatus
        {
            StatusMessage = data,
            Progress = 1
        };
        await responseStream.WriteAsync(new BackupResponse { Status = status });
    }

    public override async Task GetBackup(BackupRequest request, IServerStreamWriter<BackupResponse> responseStream, ServerCallContext context)
    {
        string filePath = string.Empty;

        try
        {
            // Подписываемся на событие прогресса для отправки клиенту и логирования
            // Подписываемся на событие для отправки клиенту и логирования
            OnProgress += async (sender, e) =>
            {
                _logger.LogInformation(e.StatusMessage);
                await responseStream.WriteAsync(new BackupResponse
                {
                    Status = new BackupStatus
                    {
                        StatusMessage = e.StatusMessage,
                        Progress = (int)e.Progress
                    }
                }, context.CancellationToken);
            };

            var backupFolder = Path.Combine(Directory.GetCurrentDirectory(), "backup");
            if (!Directory.Exists(backupFolder))
            {
                Directory.CreateDirectory(backupFolder);
                _logger.LogInformation("Created backup folder: {BackupFolder}", backupFolder);
                await ReportProgressAsync($"Created backup folder: {backupFolder}", 0);
            }

            filePath = Path.Combine(backupFolder, "backup.db");

            if (File.Exists(filePath))
            {
                await ReportProgressAsync($"Deleting existing backup file: {filePath}", 0, cancellationToken: context.CancellationToken);
                File.Delete(filePath);
            }

            // Проверка существования файла и создание, если отсутствует
            if (!File.Exists(filePath))
            {
                await ReportProgressAsync($"Backup file {filePath} not found. Creating temporary 100 GB file...", 0, cancellationToken: context.CancellationToken);
                await backupLocal.Start(filePath, responseStream);
                await ReportProgressAsync($"Temporary backup file created at {filePath} with size 100 GB.", 0, cancellationToken: context.CancellationToken);
            }


            //var fileInfo = new FileInfo(filePath);
            var fileInfo = new System.IO.FileInfo(filePath);
            long totalFileSize = fileInfo.Length;

            // Симуляция процесса бэкапа
            for (int i = 0; i <= 100; i += 10)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                await ReportProgressAsync($"Processing backup {request.BackupId}... {i}%", i, cancellationToken: context.CancellationToken);
                await Task.Delay(1000, context.CancellationToken);
            }

            // Отправка информации о начале передачи файла
            await ReportProgressAsync($"Starting file transfer for {fileInfo.Name} ({FormatFileSize(totalFileSize)})", 100, cancellationToken: context.CancellationToken);

            // Чтение и передача файла
            byte[] buffer = new byte[DefaultChunkSize];
            long totalBytesRead = 0;

            using var fileStream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: DefaultChunkSize,
                useAsync: true);

            while (totalBytesRead < totalFileSize)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                int bytesRead = await fileStream.ReadAsync(buffer, 0, DefaultChunkSize, context.CancellationToken);
                if (bytesRead == 0)
                {
                    _logger.LogWarning("Unexpected end of file reached while reading {FilePath}", filePath);
                    break;
                }

                await responseStream.WriteAsync(new BackupResponse
                {
                    Data = Google.Protobuf.ByteString.CopyFrom(buffer, 0, bytesRead)
                });

                totalBytesRead += bytesRead;

                // Логирование прогресса каждые ~10%
                if (totalFileSize > 0 && (totalBytesRead * 10 / totalFileSize) > ((totalBytesRead - bytesRead) * 10 / totalFileSize))
                {
                    _logger.LogInformation("Backup transfer progress: {Progress:F1}% ({Bytes:F2} GB of {Total:F2} GB)",
                        (double)totalBytesRead / totalFileSize * 100,
                        totalBytesRead / (1024.0 * 1024.0 * 1024.0),
                        totalFileSize / (1024.0 * 1024.0 * 1024.0));
                }
            }

            // Отправка финального сообщения о завершении
            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Backup transfer completed. Sent {FormatFileSize(totalBytesRead)}",
                    Progress = 100
                }
            });

            _logger.LogInformation("Backup transfer completed. Sent {Bytes:F2} GB to client.", totalBytesRead / (1024.0 * 1024.0 * 1024.0));
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Backup transfer cancelled by client for file {FilePath}", filePath);
            throw new RpcException(new Status(StatusCode.Cancelled, "Request cancelled by client."));
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "IO error while processing backup file {FilePath}", filePath);
            throw new RpcException(new Status(StatusCode.Internal, $"Failed to read or create backup file: {ex.Message}"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error while processing backup for file {FilePath}", filePath);
            throw new RpcException(new Status(StatusCode.Internal, $"Unexpected error: {ex.Message}"));
        }
        finally
        {
            // Отписываемся от события, чтобы избежать утечек
            OnProgress = null;
        }
    }

    private async Task CreateTemporaryFileAsync(string filePath, CancellationToken cancellationToken)
    {
        const int bufferSize = 4 * 1024 * 1024; // 4 MB
        const long targetFileSize = 100L * 1024 * 1024; // 100 GB
        byte[] buffer = new byte[bufferSize];
        long bytesWritten = 0;

        try
        {
            using var fs = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: bufferSize,
                useAsync: true);

            while (bytesWritten < targetFileSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long remainingBytes = targetFileSize - bytesWritten;
                int writeSize = (int)Math.Min(bufferSize, remainingBytes);
                await fs.WriteAsync(buffer, 0, writeSize, cancellationToken);
                bytesWritten += writeSize;

                if ((bytesWritten * 10 / targetFileSize) > ((bytesWritten - writeSize) * 10 / targetFileSize))
                {
                    _logger.LogInformation("Temporary file creation progress: {Progress:F1}% ({Bytes:F2} GB of 100 GB)",
                        (double)bytesWritten / targetFileSize * 100,
                        bytesWritten / (1024.0 * 1024.0 * 1024.0));
                }
            }

            await fs.FlushAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create temporary backup file {FilePath}", filePath);
            if (File.Exists(filePath))
            {
                try { File.Delete(filePath); } catch { }
            }
            throw;
        }
    }

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

    public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
    {
        return Task.FromResult(new PingResponse { Message = "Pong" });
    }

    public override Task<HealthResponse> CheckHealth(HealthRequest request, ServerCallContext context)
    {
        return Task.FromResult(new HealthResponse { Status = "Healthy" });
    }
}