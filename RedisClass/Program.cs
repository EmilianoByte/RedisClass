using RedisClass.Interfaces;
using RedisClass.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Get Redis configuration.
var redisConfig = builder.Configuration.GetSection("Redis");
var host = redisConfig["Host"] ?? throw new InvalidOperationException("Redis Host not configured");
var port = redisConfig["Port"] ?? throw new InvalidOperationException("Redis Port not configured");
var password = redisConfig["Password"] ?? throw new InvalidOperationException("Redis Password not configured");

// Build connection string.
var encodedPassword = Uri.EscapeDataString(password);
var connectionString = $"{host}:{port},password={encodedPassword},ssl=false,abortConnect=false";

Console.WriteLine($"[Redis] Initializing connection to {host}:{port} (SSL: disabled)");

// Initialize Redis connection.
RedisConnectionHelper.Initialize(connectionString);

// Register services for Dependency Injection.
builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
    RedisConnectionHelper.Connection);

builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IConnectionMultiplexer>().GetDatabase());

builder.Services.AddScoped<ITaskService, TaskService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<ILeaderboardService, LeaderboardService>();

// Add controllers and API documentation.
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo
    {
        Title = "Redis Class",
        Version = "v1",
        Description = "A simple To-Do List API using Redis for storage"
    });
});

var app = builder.Build();

// Test Redis connection at startup.
try
{
    var db = RedisConnectionHelper.Database;
    var pingResult = await db.PingAsync();
    Console.WriteLine($"Redis connection successful (ping: {pingResult.TotalMilliseconds}ms)");
}
catch (Exception ex)
{
    Console.WriteLine($"Redis connection failed: {ex.Message}");
    Console.WriteLine("Application will start but Redis features will not work");
}

// Configure HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Redis Class v1");
        options.RoutePrefix = "swagger";
    });
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
