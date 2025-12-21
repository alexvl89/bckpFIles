#### Systemd Credentials

##### Linux (systemd с credentials)

Современный и безопасный способ управления секретами в systemd (требуется systemd >= 250):

Создайте файл сервиса `/etc/systemd/system/backup-server.service`:

```ini
[Unit]
Description=Backup Server
After=network.target

[Service]
Type=simple
User=backup
WorkingDirectory=/opt/backup-server
ExecStart=/usr/bin/dotnet /opt/backup-server/BackupServer.dll
Restart=always
RestartSec=10

# Загрузка credentials
LoadCredential=db_password:/etc/backup-server/secrets/db_password
LoadCredential=cert_password:/etc/backup-server/secrets/cert_password

# Передача credentials как переменные окружения
EnvironmentFiles=-/etc/backup-server/backup-server.env
Environment="BackupSettings__DbPassword=${CREDENTIALS_DIRECTORY}/db_password"
Environment="Kestrel__Endpoints__gRPC__Certificate__Password=${CREDENTIALS_DIRECTORY}/cert_password"

# Безопасность
PrivateTmp=yes
NoNewPrivileges=true
ProtectSystem=strict
ProtectHome=yes
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
ReadWritePaths=/opt/backup-server/logs

[Install]
WantedBy=multi-user.target
```

Создайте файлы с секретами:

```bash
sudo mkdir -p /etc/backup-server/secrets
sudo sh -c 'echo "your_db_password" > /etc/backup-server/secrets/db_password'
sudo sh -c 'echo "your_cert_password" > /etc/backup-server/secrets/cert_password'
sudo chmod 600 /etc/backup-server/secrets/*
sudo chown backup:backup /etc/backup-server/secrets -R
```

Обновите `Program.cs` для чтения credentials из файлов:

```csharp
// filepath: d:\Prj\Сsharp\bckpFIles\BackupServer\Program.cs
// ...existing code...

var builder = WebApplication.CreateBuilder(args);

// Загрузка credentials из systemd
var credentialsDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
if (!string.IsNullOrEmpty(credentialsDir))
{
    var dbPasswordFile = Path.Combine(credentialsDir, "db_password");
    var certPasswordFile = Path.Combine(credentialsDir, "cert_password");

    if (File.Exists(dbPasswordFile))
    {
        var dbPassword = File.ReadAllText(dbPasswordFile).Trim();
        Environment.SetEnvironmentVariable("BackupSettings__DbPassword", dbPassword);
    }

    if (File.Exists(certPasswordFile))
    {
        var certPassword = File.ReadAllText(certPasswordFile).Trim();
        Environment.SetEnvironmentVariable("Kestrel__Endpoints__gRPC__Certificate__Password", certPassword);
    }
}

builder.Configuration.AddEnvironmentVariables();

// ...existing code...
```

Команды для управления сервисом:

```bash
sudo systemctl daemon-reload
sudo systemctl enable backup-server
sudo systemctl start backup-server
sudo systemctl status backup-server

# Просмотр логов
sudo journalctl -u backup-server -f

# Проверка статуса credentials
sudo systemctl show -p LoadCredential backup-server
```

##### Проверка работы systemd credentials

Создайте тестовый скрипт для проверки корректности загрузки credentials:

```csharp
// filepath: d:\Prj\Сsharp\bckpFIles\BackupServer\Services\CredentialsLoader.cs
using System;
using System.IO;

namespace BackupServer.Services
{
    public class CredentialsLoader
    {
        public static void LoadFromSystemdCredentials(ILogger<Program> logger)
        {
            var credentialsDir = Environment.GetEnvironmentVariable("CREDENTIALS_DIRECTORY");
            
            if (string.IsNullOrEmpty(credentialsDir))
            {
                logger.LogWarning("CREDENTIALS_DIRECTORY не установлена. Credentials не загружены.");
                return;
            }

            logger.LogInformation("Загрузка credentials из: {CredentialsDir}", credentialsDir);

            try
            {
                // Список ожидаемых credentials
                var credentialFiles = new[] { "db_password", "cert_password" };

                foreach (var credFile in credentialFiles)
                {
                    var credPath = Path.Combine(credentialsDir, credFile);
                    
                    if (File.Exists(credPath))
                    {
                        var credContent = File.ReadAllText(credPath).Trim();
                        var envVarName = credFile switch
                        {
                            "db_password" => "BackupSettings__DbPassword",
                            "cert_password" => "Kestrel__Endpoints__gRPC__Certificate__Password",
                            _ => credFile
                        };
                        
                        Environment.SetEnvironmentVariable(envVarName, credContent);
                        logger.LogInformation("✓ Credential '{CredentialFile}' успешно загружен в {EnvVar}", 
                            credFile, envVarName);
                    }
                    else
                    {
                        logger.LogWarning("✗ Credential '{CredentialFile}' не найден в {CredentialsDir}", 
                            credFile, credentialsDir);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Ошибка при загрузке credentials из systemd");
                throw;
            }
        }
    }
}
```

Используйте в Program.cs:

```csharp
// ...existing code...
var builder = WebApplication.CreateBuilder(args);
var logger = LoggerFactory.Create(c => c.AddConsole()).CreateLogger<Program>();

CredentialsLoader.LoadFromSystemdCredentials(logger);

// ...existing code...
```