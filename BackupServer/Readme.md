# BackupServer

BackupServer — это приложение на .NET, предназначенное для работы с бэкапами. Оно запускается на Kestrel-сервере и поддерживает работу в Docker-контейнере.

## Требования

- .NET 9.0 SDK 
- Docker (для контейнеризации)

## Конфигурация

### appsettings.json

Файл `appsettings.json` содержит основные настройки приложения:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://*:5000"
      },
      "gRPC": {
        "Url": "https://*:5001",
        "Protocols": "Http2",
        "Certificate": {
          "Path": "grpc-cert.pfx"
        }
      }
    }
  },
  "BackupSettings": {
    "DbUser": "postgres",
    "DbName": "MyDb",
    "PgDumpPath": "/usr/bin/pg_dump",
    "BackupPath": "/backups",
    "DbHost": "127.0.0.1",
    "DbPort": "5432"
  }
}
```

### Безопасность

Пароли базы данных и сертификатов не должны храниться в `appsettings.json`. Используйте переменные окружения или секреты .NET.

Примеры:
- Для пароля БД: `export BackupSettings__DbPassword=yourpassword`
- Для пароля сертификата: `export Kestrel__Endpoints__gRPC__Certificate__Password=yourcertpassword`

Для разработки используйте `dotnet user-secrets`:
```
dotnet user-secrets set "BackupSettings:DbPassword" "yourpassword"
```


### Изменения

#### 1.0.0-2
1. Добавлен сервис для бекапа при нахождении локально
1. Добавлены настройки в appsettings.json
1. Требуется отладка

#### 1.0.0-1
1. Добавлена возможность выполнения бекапа из docker-контейнера postgres.
1. Проверена передача и восстановление бекапа.