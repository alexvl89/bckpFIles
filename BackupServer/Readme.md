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
  "AllowedHosts": "localhost",
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:5000"
      }
    }
  }
}