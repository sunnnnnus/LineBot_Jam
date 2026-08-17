using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Linebot_jam.Data;
using Linebot_jam.Options;
using Linebot_jam.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
