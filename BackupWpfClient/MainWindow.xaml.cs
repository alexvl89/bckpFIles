using BackupServer;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;

namespace BackupWpfClient
{
    public partial class MainWindow : Window
    {
        private readonly ILogger<MainWindow> _logger;
        private IConfiguration? _config;

        public MainWindow()
        {
            InitializeComponent();

            var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            _logger = loggerFactory.CreateLogger<MainWindow>();

            LoadConfig();
        }

        private void LoadConfig()
        {
            _config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .Build();

            if (!string.IsNullOrEmpty(_config["GrpcServer:Address"]))
            {
                ServerAddressBox.Text = _config["GrpcServer:Address"];
            }
        }

        private async void RunBackup_Click(object sender, RoutedEventArgs e)
        {
            LogBox.Clear();

            string serverAddress = ServerAddressBox.Text.Trim();
            string backupId = DatabaseNameBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(serverAddress))
            {
                UpdateStatus("Ошибка: укажите адрес сервера!");
                return;
            }

            if (string.IsNullOrWhiteSpace(backupId))
            {
                UpdateStatus("Ошибка: укажите имя базы!");
                return;
            }

            UpdateStatus("Подключение к серверу...");

            try
            {
                AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };

                using var channel = GrpcChannel.ForAddress(serverAddress, new GrpcChannelOptions
                {
                    HttpHandler = handler
                });

                var client = new Backup.BackupClient(channel);

                UpdateStatus("Выполняем Ping...");
                var ping = await client.PingAsync(new PingRequest());
                Log($"Ping ответ: {ping.Message}");

                UpdateStatus("Проверяем состояние сервера...");
                var health = await client.CheckHealthAsync(new HealthRequest());
                Log($"Статус сервера: {health.Status}");

                UpdateStatus($"Запрос бэкапа: {backupId}");
                using var call = client.GetBackup(new BackupRequest { BackupId = backupId });

                string filePath = $"backup_{backupId}.db";
                long totalBytes = 0;

                using var fileStream = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);

                await foreach (var response in call.ResponseStream.ReadAllAsync())
                {
                    if (response.Status != null)
                    {
                        UpdateStatus($"{response.Status.Progress}% — {response.Status.StatusMessage}");
                    }
                    else if (response.Data.Length > 0)
                    {
                        await fileStream.WriteAsync(response.Data.Memory);
                        totalBytes += response.Data.Length;
                    }
                }

                await fileStream.FlushAsync();
                UpdateStatus("Готово");
                Log($"Файл сохранён: {filePath}");
                Log($"Размер: {totalBytes / (1024.0 * 1024.0):F2} MB");
            }
            catch (RpcException ex)
            {
                UpdateStatus("Ошибка gRPC");
                Log($"gRPC error: {ex.Status.StatusCode} — {ex.Status.Detail}");
            }
            catch (IOException ex)
            {
                UpdateStatus("Ошибка записи файла");
                Log($"IO ошибка: {ex.Message}");
            }
            catch (Exception ex)
            {
                UpdateStatus("Неожиданная ошибка");
                Log($"Ошибка: {ex.Message}");
            }
        }

        private void UpdateStatus(string message)
        {
            StatusText.Text = message;
        }

        private void Log(string message)
        {
            LogBox.AppendText(message + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
    }
}
