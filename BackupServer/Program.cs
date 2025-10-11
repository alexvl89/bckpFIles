using BackupServer;
using BackupServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net;

var builder = WebApplication.CreateBuilder(args);


// Добавление конфигурации
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);

// Добавляем gRPC сервисы
builder.Services.AddGrpc();
builder.Services.AddSingleton<IBackupServiceLocal, BackupLocal>();


//// Настраиваем Kestrel для gRPC (HTTP/2)
//builder.WebHost.ConfigureKestrel(options =>
//{
//    options.ListenAnyIP(5001, o =>
//    {
//        o.Protocols = HttpProtocols.Http2;
//        // Для продакшена рекомендуется включить HTTPS
//        // o.UseHttps("certificate.pfx", "password");
//    });
//});

var app = builder.Build();

// Маппинг gRPC сервиса
app.MapGrpcService<BackupService>();

// Добавляем endpoint для проверки статуса
app.MapGet("/", () => "Backup Server is running.");

// Обработка ошибок
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("An error occurred. Please try again later.");
    });
});

await app.RunAsync();