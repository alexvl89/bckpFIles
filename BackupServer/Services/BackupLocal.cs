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

public class BackupLocal : IBackupServiceLocal
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
        string dbHost = settings["DbHost"] ?? "127.0.0.1";
        string dbPort = settings["DbPort"] ?? "5432";
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
            _logger.LogInformation("Запускаем процесс бэкапа '{DbName}' в '{FullOutputPath}'", dbName, fullOutputPath);

            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Starting backup process for database '{dbName}'...",
                    Progress = 1
                }
            });

            long previousSize = File.Exists(fullOutputPath) ? new System.IO.FileInfo(fullOutputPath).Length : 0;
            if (previousSize > 0)
            {
                _logger.LogInformation("Previous backup size: {PreviousSize:F2} KB", previousSize / 1024.0);
            }

            // Формируем команду для pg_dump (указываем хост и порт!)
            string pgDumpArgs = $"-h {dbHost} -p {dbPort} -U {dbUser} -F c {dbName}";

            // Логируем все параметры
            _logger.LogInformation("pg_dump path: {PgDumpPath}", pgDumpPath);
            _logger.LogInformation("pg_dump args: {PgDumpArgs}", pgDumpArgs);
            _logger.LogInformation("Database: {DbName}, Host: {DbHost}, Port: {DbPort}, User: {DbUser}", dbName, dbHost, dbPort, dbUser);
            _logger.LogInformation("PGPASSWORD (set? {HasPassword})", !string.IsNullOrEmpty(dbPassword));

            // Настройка процесса pg_dump
            var startInfo = new ProcessStartInfo
            {
                FileName = pgDumpPath,
                Arguments = pgDumpArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Передаём пароль в окружение
            startInfo.Environment["PGPASSWORD"] = dbPassword ?? string.Empty;

            _logger.LogInformation("Process environment contains PGPASSWORD: {ContainsPGPASSWORD}",
                startInfo.Environment.ContainsKey("PGPASSWORD"));

            using var process = new Process { StartInfo = startInfo };

            _logger.LogInformation("Executing: {PgDumpPath} {PgDumpArgs}", pgDumpPath, pgDumpArgs);
            process.Start();

            long totalBytesWritten = 0;

            using (var fileStream = new FileStream(fullOutputPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var progressTask = Task.Run(async () =>
                {
                    await Task.Delay(200);
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

                        await Task.Delay(2000);
                        if (process.HasExited) break;
                    }
                });

                byte[] buffer = new byte[8192];
                int bytesRead;
                while ((bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead);
                    Interlocked.Add(ref totalBytesWritten, bytesRead);
                }

                await progressTask;
            }

            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                _logger.LogError("pg_dump exited with code {ExitCode}. Error output:\n{Error}", process.ExitCode, error);
                await responseStream.WriteAsync(new BackupResponse
                {
                    Status = new BackupStatus
                    {
                        StatusMessage = $"Backup failed (exit code {process.ExitCode}): {error}",
                        Progress = -1
                    }
                });
                throw new Exception($"pg_dump failed (code {process.ExitCode}): {error}");
            }

            var fileInfo = new System.IO.FileInfo(fullOutputPath);
            _logger.LogInformation("Backup completed successfully! Size: {FinalSize:F2} KB", fileInfo.Length / 1024.0);

            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Backup completed successfully! Size: {fileInfo.Length / 1024.0:F2} KB",
                    Progress = 100
                }
            });

            return fullOutputPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Backup failed: {Message}", ex.Message);
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
