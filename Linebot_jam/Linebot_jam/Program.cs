using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Linebot_jam.Data;
using Linebot_jam.Options;
using Linebot_jam.Services;

var builder = WebApplication.CreateBuilder(args);

// Render assigns the listen port via $PORT; fall back to 8080 for local container runs.
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// Add services to the container.

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrEmpty(connectionString))
{
    var dbHost = Environment.GetEnvironmentVariable("DB_HOST");
    if (!string.IsNullOrEmpty(dbHost))
    {
        connectionString =
            $"Host={dbHost};" +
            $"Port={Environment.GetEnvironmentVariable("DB_PORT")};" +
            $"Database={Environment.GetEnvironmentVariable("DB_NAME")};" +
            $"Username={Environment.GetEnvironmentVariable("DB_USER")};" +
            $"Password={Environment.GetEnvironmentVariable("DB_PASSWORD")}";
    }
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.Configure<LineOptions>(builder.Configuration.GetSection("Line"));
builder.Services.AddSingleton<ILineSignatureValidator, LineSignatureValidator>();
builder.Services.AddHttpClient<ILineMessagingClient, LineMessagingClient>((sp, client) =>
{
    var lineOptions = sp.GetRequiredService<IOptions<LineOptions>>().Value;
    client.BaseAddress = new Uri("https://api.line.me/");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", lineOptions.ChannelAccessToken);
});

builder.Services.Configure<GeminiOptions>(builder.Configuration.GetSection("Gemini"));
builder.Services.AddHttpClient<IGeminiClient, GeminiClient>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
});

builder.Services.AddHostedService<ReminderBackgroundService>();

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Render terminates TLS at its edge and forwards plain HTTP internally,
// so redirecting to HTTPS inside the container would be wrong here.
if (app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseAuthorization();

app.MapGet("/health", () => Results.Ok("healthy"));
app.MapControllers();

app.Run();
