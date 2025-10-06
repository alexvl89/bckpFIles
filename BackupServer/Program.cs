using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Hosting;
using BackupServer;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);

// Добавляем gRPC сервисы
builder.Services.AddGrpc();

// Настраиваем Kestrel для gRPC (HTTP/2)
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenLocalhost(5001, o =>
    {
        o.Protocols = HttpProtocols.Http2;
        // Для продакшена рекомендуется включить HTTPS
        // o.UseHttps("certificate.pfx", "password");
    });
});

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