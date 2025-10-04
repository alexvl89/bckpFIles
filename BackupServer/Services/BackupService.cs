using Grpc.Core;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;


namespace BackupServer
{
    public class BackupService : Backup.BackupBase
    {

        private readonly ILogger<BackupService> _logger;
        private const long MaxFileSize = 1 * 1024 * 1024;
        private const int DefaultChunkSize = 1 * 1024 * 1024; // 1 MB

        public BackupService(ILogger<BackupService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override async Task GetBackup(BackupRequest request, IServerStreamWriter<BackupResponse> responseStream, ServerCallContext context)
        {
            string filePath = string.Empty;

            try
            {
                var backupFolder = Path.Combine(Directory.GetCurrentDirectory(), "backup");
                if (!Directory.Exists(backupFolder))
                {
                    Directory.CreateDirectory(backupFolder);
                }
                Console.WriteLine($"Backup folder: {backupFolder}");


                // Путь к файлу бэкапа
                filePath = Path.Combine(backupFolder, "backup.db");

                // Check if file exists, create if it doesn't
                if (!File.Exists(filePath))
                {
                    _logger.LogInformation("Backup file {FilePath} not found. Creating temporary 100 GB file...", filePath);
                    await CreateTemporaryFileAsync(filePath, context.CancellationToken);
                    _logger.LogInformation("Temporary backup file created at {FilePath} with size 100 GB.", filePath);
                }

                // Validate file size
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSize)
                {
                    throw new RpcException(new Status(StatusCode.InvalidArgument,
                        $"Backup file size {fileInfo.Length / (1024.0 * 1024.0 * 1024.0):F2} GB exceeds maximum allowed size of 100 GB."));
                }

                // Read and stream file
                byte[] buffer = new byte[DefaultChunkSize];
                long totalBytesRead = 0;
                long totalFileSize = fileInfo.Length;

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

                    // Log progress every 10%
                    if (totalFileSize > 0 && (totalBytesRead * 10 / totalFileSize) > ((totalBytesRead - bytesRead) * 10 / totalFileSize))
                    {
                        _logger.LogInformation("Backup transfer progress: {Progress:F1}% ({Bytes:F2} GB of {Total:F2} GB)",
                            (double)totalBytesRead / totalFileSize * 100,
                            totalBytesRead / (1024.0 * 1024.0 * 1024.0),
                            totalFileSize / (1024.0 * 1024.0 * 1024.0));
                    }
                }

                _logger.LogInformation("Backup transfer completed. Sent {Bytes:F2} GB to client.",
                    totalBytesRead / (1024.0 * 1024.0 * 1024.0));
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
        }


        /// <summary>
        /// Асинхронно создает временный файл заданного размера (100 ГБ), заполняя его нулями.
        /// </summary>
        /// <param name="filePath">Путь к создаваемому файлу.</param>
        /// <param name="cancellationToken">Токен отмены операции.</param>
        private async Task CreateTemporaryFileAsync(string filePath, CancellationToken cancellationToken)
        {
            const int bufferSize = 4 * 1024 * 1024; // Размер буфера для записи: 4 МБ
            byte[] buffer = new byte[bufferSize];   // Буфер, заполненный нулями
            long bytesWritten = 0;                  // Счетчик записанных байт

            try
            {
                // Открываем поток для создания файла
                using var fs = new FileStream(
                    filePath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: bufferSize,
                    useAsync: true);

                // Записываем данные в файл до достижения нужного размера
                while (bytesWritten < MaxFileSize)
                {
                    cancellationToken.ThrowIfCancellationRequested(); // Проверка отмены

                    long remainingBytes = MaxFileSize - bytesWritten; // Сколько осталось записать
                    int writeSize = (int)Math.Min(bufferSize, remainingBytes); // Размер текущей записи
                    await fs.WriteAsync(buffer, 0, writeSize, cancellationToken); // Асинхронная запись
                    bytesWritten += writeSize; // Обновляем счетчик

                    // Логируем прогресс каждые 10%
                    if ((bytesWritten * 10 / MaxFileSize) > ((bytesWritten - writeSize) * 10 / MaxFileSize))
                    {
                        _logger.LogInformation("Temporary file creation progress: {Progress:F1}% ({Bytes:F2} GB of 100 GB)",
                            (double)bytesWritten / MaxFileSize * 100,
                            bytesWritten / (1024.0 * 1024.0 * 1024.0));
                    }
                }

                await fs.FlushAsync(cancellationToken); // Финализируем запись
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create temporary backup file {FilePath}", filePath);
                // Удаляем частично созданный файл при ошибке
                if (File.Exists(filePath))
                {
                    try { File.Delete(filePath); } catch { }
                }
                throw;
            }
        }

        // Метод Ping для проверки доступности
        public override Task<PingResponse> Ping(PingRequest request, ServerCallContext context)
        {
            return Task.FromResult(new PingResponse { Message = "Pong" });
        }

        // Метод CheckHealth для проверки состояния сервера
        public override Task<HealthResponse> CheckHealth(HealthRequest request, ServerCallContext context)
        {
            // Здесь можно добавить логику проверки состояния сервера
            // Например, проверка подключения к базе данных или хранилищу
            return Task.FromResult(new HealthResponse { Status = "Healthy" });
        }
    }
}