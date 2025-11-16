# BackupServer & BackupClient

## Описание

Решение состоит из двух проектов:
- **BackupServer** — серверная часть на C#, реализующая gRPC-сервис для создания и передачи бэкапа базы данных. Поддерживает подключение по SSH для выполнения команд бэкапа и скачивания файлов.
- **BackupClient** — клиентская часть на C#, запрашивающая бэкап у сервера через gRPC и сохраняющая полученный файл.

## Структура

- `BackupServer` — сервер gRPC, поддерживает SSH-бэкап.
- `BackupClient` — gRPC-клиент, получает и сохраняет бэкап.
- `Protos/backup.proto` — описание gRPC-сервиса.
- `BackupClient/appsettings.json` — конфигурация адреса сервера.

## Запуск

1. Установите зависимости:
   ```
   dotnet restore
   ```

2. Запустите сервер:
   ```
   dotnet run --project BackupServer
   ```

3. Убедитесь, что файл бэкапа (`backup.db`) доступен на сервере.

4. Настройте адрес сервера в `BackupClient/appsettings.json`.

5. Запустите клиент:
   ```
   dotnet run --project BackupClient
   ```

## Используемые технологии

- .NET 10
- gRPC
- SSH.NET (для SSH/SFTP)
- Protobuf
