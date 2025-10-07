using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace BackupServer.Services;

public class BackupServiceWithDocker
{
    public static async Task<string> Start(string outputFile, Grpc.Core.IServerStreamWriter<BackupResponse> responseStream)
    {
        // Заданные аргументы
        string containerName = "dbpostgres"; // Имя Docker-контейнера
        string dbName = "MyDb";            // Имя базы данных
        string dbUser = "postgres";         // Пользователь базы данных
        string dbPassword = "postgres";     // Пароль

        try
        {
            // Получение абсолютного пути для файла бэкапа
            string fullOutputPath = Path.GetFullPath(outputFile);
            Console.WriteLine($"Starting backup process for database '{dbName}'...");
            Console.WriteLine($"Backup will be saved to: {fullOutputPath}");

            // Отправка информации о начале передачи файла
            await responseStream.WriteAsync(new BackupResponse
            {
                Status = new BackupStatus
                {
                    StatusMessage = $"Starting backup process for database '{dbName}'...",
                    Progress = 1
                }
            });

            // Проверка существования предыдущего бэкапа
            long previousSize = File.Exists(outputFile) ? new System.IO.FileInfo(outputFile).Length : 0;
            if (previousSize > 0)
            {
                Console.WriteLine($"Previous backup size: {previousSize / 1024.0:F2} KB");
            }

            // !!! ИСПРАВЛЕНИЕ: Добавлен флаг -F c для создания бэкапа в Custom формате.
            // Этот формат является бинарным, сжатым и полностью поддерживается pg_restore / PGAdmin.
            string pgDumpCommand = $"pg_dump -U {dbUser} -F c {dbName}";

            // Запуск процесса docker exec с передачей PGPASSWORD через environment
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = "docker",
                // Используем -i для интерактивного режима, чтобы пайп работал корректно
                Arguments = $"exec -i {containerName} bash -c \"PGPASSWORD={dbPassword} {pgDumpCommand}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process { StartInfo = startInfo })
            {
                Console.WriteLine($"Executing command: docker exec -i {containerName} bash -c \"PGPASSWORD=*** {pgDumpCommand}\"");
                process.Start();

                long totalBytesWritten = 0;
                using (FileStream fileStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                {
                    // Запуск задачи для отображения прогресса каждую секунду
                    var progressTask = Task.Run(async () =>
                    {
                        // Ожидаем начала записи, чтобы избежать лишних сообщений
                        await Task.Delay(100);
                        while (!process.HasExited || totalBytesWritten > 0)
                        {
                            Console.WriteLine($"Progress: {totalBytesWritten / 1024.0:F2} KB written...");

                            // Отправка информации о начале передачи файла
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

                    // Чтение потока вывода (StandardOutput) и запись данных в файл
                    byte[] buffer = new byte[8192]; // 8KB буфер
                    int bytesRead;
                    while ((bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead);
                        Interlocked.Add(ref totalBytesWritten, bytesRead); // Потокобезопасное обновление
                    }

                    // Ожидаем завершения задачи прогресса
                    await progressTask;
                }

                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    Console.WriteLine("---------------------------------------------");
                    Console.WriteLine($"Backup FAILED (Exit Code: {process.ExitCode})");
                    Console.WriteLine($"Error Details: {error}");
                    Console.WriteLine("---------------------------------------------");
                }
                else
                {
                    // Проверка финального размера файла
                    System.IO.FileInfo fileInfo = new(outputFile);
                    Console.WriteLine($"\n=============================================");
                    Console.WriteLine($"Backup completed successfully!");
                    Console.WriteLine($"Final backup size: {fileInfo.Length / 1024.0:F2} KB");
                    Console.WriteLine($"Backup saved to: {fullOutputPath}");
                    Console.WriteLine($"=============================================");

                    // Сравнение с предыдущим размером
                    if (previousSize > 0)
                    {
                        double sizeDifference = fileInfo.Length - previousSize;
                        Console.WriteLine($"Size change: {(sizeDifference / 1024.0):F2} KB " +
                            $"(new size is {(sizeDifference > 0 ? "larger" : sizeDifference < 0 ? "smaller" : "the same")})");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Backup failed with exception: {ex.Message}");
        }

        return outputFile;
    }
}
