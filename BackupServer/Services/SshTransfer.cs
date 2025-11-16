using Renci.SshNet;
using System;
using System.IO;

namespace BackupServer.Services;
// Класс для подключения по SSH и выполнения бэкапа
public class SshTransfer
{
    private readonly string _host;
    private readonly string _username;
    private readonly string _password;

    public SshTransfer(string host, string username, string password)
    {
        _host = host;
        _username = username;
        _password = password;
    }

    // Выполнить команду бэкапа на удалённом сервере и скачать файл
    public bool ExecuteBackup(string backupCommand, string remoteFilePath, string localFilePath)
    {
        using (var client = new SshClient(_host, _username, _password))
        {
            client.Connect();
            // Выполнение команды бэкапа (например, pg_dump, mysqldump и т.д.)
            var cmd = client.CreateCommand(backupCommand);
            var result = cmd.Execute();
            // Проверка на ошибки
            if (!string.IsNullOrEmpty(cmd.Error))
            {
                Console.WriteLine($"Ошибка SSH: {cmd.Error}");
                client.Disconnect();
                return false;
            }
            client.Disconnect();
        }

        // Скачивание файла через SFTP
        using (var sftp = new SftpClient(_host, _username, _password))
        {
            sftp.Connect();
            using (var file = File.OpenWrite(localFilePath))
            {
                sftp.DownloadFile(remoteFilePath, file);
            }
            sftp.Disconnect();
        }
        return true;
    }
}