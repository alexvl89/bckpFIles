using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Grpc.Core;


namespace BackupServer.Services;

public interface IBackupServiceLocal
{
    Task<string> Start(string outputFile, IServerStreamWriter<BackupResponse> responseStream);
}

public class BackupLocal: IBackupServiceLocal
{
    private readonly ILogger<BackupLocal> _logger;
    private readonly IConfiguration _configuration;

    public BackupLocal(ILogger<BackupLocal> logger, IConfiguration configuration)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public async Task<string> Start(string outputFile, IServerStreamWriter<BackupResponse> responseStream)
    {
        // Чтение настроек из appsettings.json
        var settings = _configuration.GetSection("BackupSettings");
        string dbUser = settings["DbUser"];
        string dbName = settings["DbName"];
        string dbPassword = settings["DbPassword"];
        string pgDumpPath = settings["PgDumpPath"];
        string backupPath = settings["BackupPath"];

        // Формирование полного пути для файла бэкапа
        string fullOutputPath = Path.Combine(backupPath, outputFile);
        if (!Directory.Exists(backupPath))
        {
            Directory.CreateDirectory(backupPath);
            _logger.LogInformation("Создан каталог: {BackupPath}", backupPath);
        }

        try
        {
            _logger.LogInformation("Запускаем процесс бекапа '{DbName}' to '{FullOutputPath}'", dbName, fullOutputPath);
            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Starting backup process for database '{dbName}'...",
                    Progress = 1
                }
            });

            // Проверка существования предыдущего бэкапа
            long previousSize = File.Exists(fullOutputPath) ? new System.IO.FileInfo(fullOutputPath).Length : 0;
            if (previousSize > 0)
            {
                _logger.LogInformation("Previous backup size: {PreviousSize:F2} KB", previousSize / 1024.0);
            }

            // Формирование команды для pg_dump
            string pgDumpArgs = $"-U {dbUser} -F c {dbName}";

            // Настройка процесса pg_dump
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = pgDumpPath,
                Arguments = pgDumpArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment = { { "PGPASSWORD", dbPassword } } // Передача пароля через переменную окружения
            };

            using (Process process = new Process { StartInfo = startInfo })
            {
                _logger.LogInformation("Executing pg_dump: {PgDumpPath} {PgDumpArgs}", pgDumpPath, pgDumpArgs);
                process.Start();

                long totalBytesWritten = 0;
                using (FileStream fileStream = new FileStream(fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    // Запуск задачи для отображения прогресса каждые 2 секунды
                    var progressTask = Task.Run(async () =>
                    {
                        await Task.Delay(100); // Небольшая задержка перед началом
                        while (!process.HasExited || totalBytesWritten > 0)
                        {
                            _logger.LogInformation("Progress: {TotalBytesWritten:F2} KB written", totalBytesWritten / 1024.0);
                            await responseStream.WriteAsync(new BackupResponse
                            {
                                Status = new BackupStatus
                                {
                                    StatusMessage = $"Progress: {totalBytesWritten / 1024.0:F2} KB written",
                                    Progress = 2
                                }
                            });

                            await Task.Delay(2000); // Обновление каждые 2 секунды
                            if (process.HasExited && totalBytesWritten > 0) break;
                            if (process.HasExited && process.ExitCode != 0) break;
                        }
                    });

                    // Чтение потока вывода и запись в файл
                    byte[] buffer = new byte[8192]; // 8KB буфер
                    int bytesRead;
                    while ((bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        Interlocked.Add(ref totalBytesWritten, bytesRead); // Потокобезопасное обновление
                    }

                    // Ожидание завершения задачи прогресса
                    await progressTask;
                }

                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    _logger.LogError("Backup failed with exit code {ExitCode}. Error: {Error}", process.ExitCode, error);
                    await responseStream.WriteAsync(new BackupResponse
                    {
                        Status = new BackupStatus
                        {
                            StatusMessage = $"Backup failed with exit code {process.ExitCode}. Error: {error}",
                            Progress = -1
                        }
                    });
                    throw new Exception($"Backup failed with exit code {process.ExitCode}: {error}");
                }

                // Проверка финального размера файла
                System.IO.FileInfo fileInfo = new(fullOutputPath);
                _logger.LogInformation("Backup completed successfully! Final backup size: {FinalSize:F2} KB, saved to: {FullOutputPath}", fileInfo.Length / 1024.0, fullOutputPath);
                await responseStream.WriteAsync(new BackupResponse
                {
                    Status = new BackupStatus
                    {
                        StatusMessage = $"Backup completed successfully! Final backup size: {fileInfo.Length / 1024.0:F2} KB",
                        Progress = 100
                    }
                });

                // Сравнение с предыдущим размером
                if (previousSize > 0)
                {
                    double sizeDifference = fileInfo.Length - previousSize;
                    _logger.LogInformation("Size change: {SizeDifference:F2} KB ({ChangeDirection})",
                        sizeDifference / 1024.0,
                        sizeDifference > 0 ? "larger" : sizeDifference < 0 ? "smaller" : "the same");
                }
            }

            return fullOutputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed with exception: {Message}", ex.Message);
            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Backup failed: {ex.Message}",
                    Progress = -1
                }
            });
            throw;
        }
        finally
        {
            // Очистка переменной окружения
            Environment.SetEnvironmentVariable("PGPASSWORD", null);
        }
    }
}
