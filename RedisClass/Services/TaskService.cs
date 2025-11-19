using RedisClass.Interfaces;
using RedisClass.Models;
using StackExchange.Redis;
using System.Text.Json;

namespace RedisClass.Services;

public class TaskService(IDatabase redis, ILogger<TaskService> logger) : ITaskService
{
    private readonly IDatabase _redis = redis;
    private readonly ILogger<TaskService> _logger = logger;

    // Redis key naming conventions.
    private const string TaskPrefix = "task:";
    private const string TaskIdsKey = "task:ids";
    private const string StatsCreatedKey = "stats:tasks:created";
    private const string StatsCompletedKey = "stats:tasks:completed";

    // This will be a suffix because the key will be something like task:ID:{suffix}.
    private const string TaskLogSuffix = ":log";

    // JsonSerializerOptions for consistent serialization.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public async Task<TaskItem?> GetTaskAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Task ID cannot be null or empty", nameof(id));
        }

        var key = GetTaskKey(id);
        var json = await _redis.StringGetAsync(key);

        if (json.IsNullOrEmpty)
        {
            return null;
        }

        return JsonSerializer.Deserialize<TaskItem>(json!, JsonOptions);
    }

    public async Task<IEnumerable<TaskItem>> GetAllTasksAsync()
    {
        // Get all task IDs from the Set.
        var taskIds = await _redis.SetMembersAsync(TaskIdsKey);

        if (taskIds.Length == 0)
        {
            return Enumerable.Empty<TaskItem>();
        }

        // Batch fetch for performance (single network round-trip)
        var keys = taskIds.Select(id => (RedisKey)GetTaskKey(id.ToString())).ToArray();
        var values = await _redis.StringGetAsync(keys);

        var tasks = new List<TaskItem>();

        foreach (var value in values)
        {
            if (!value.IsNullOrEmpty)
            {
                var task = JsonSerializer.Deserialize<TaskItem>(value!, JsonOptions);
                if (task != null)
                {
                    tasks.Add(task);
                }
            }
        }

        return tasks;
    }

    public async Task<List<string>> GetActivityLogAsync(string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            throw new ArgumentException("Task ID cannot be empty", nameof(taskId));

        // Create key.
        var logKey = $"{TaskPrefix}{taskId}{TaskLogSuffix}";

        // Read all.
        var entries = await _redis.ListRangeAsync(logKey, 0, -1);

        return entries.Select(e => e.ToString()).ToList();
    }

    public async Task<TaskItem> CreateTaskAsync(TaskItem task)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        if (string.IsNullOrWhiteSpace(task.Title))
        {
            throw new ArgumentException("Task title is required", nameof(task));
        }

        // Generate ID and set timestamps
        task.Id = Guid.NewGuid().ToString();
        task.CreatedAt = DateTime.UtcNow;
        task.CompletedAt = null;
        task.IsCompleted = false;

        var key = GetTaskKey(task.Id);
        var json = JsonSerializer.Serialize(task, JsonOptions);

        //await _redis.StringSetAsync(key, json);
        //await _redis.SetAddAsync(TaskIdsKey, task.Id);
        //await _redis.StringIncrementAsync(StatsCreatedKey);

        // Atomic transaction: save task + add to index + increment counter
        var transaction = _redis.CreateTransaction();

        _ = transaction.StringSetAsync(key, json);
        _ = transaction.SetAddAsync(TaskIdsKey, task.Id);
        _ = transaction.StringIncrementAsync(StatsCreatedKey);

        bool committed = await transaction.ExecuteAsync();

        if (!committed)
        {
            _logger.LogWarning("Transaction failed when creating task {TaskId}", task.Id);
            throw new InvalidOperationException("Failed to create task in Redis");
        }

        _logger.LogInformation("Created task {TaskId}: {TaskTitle}", task.Id, task.Title);

        await AddActivityLogAsync(task.Id, "Task created");

        return task;
    }

    public async Task<bool> UpdateTaskAsync(TaskItem task)
    {
        if (task == null)
        {
            throw new ArgumentNullException(nameof(task));
        }

        var key = GetTaskKey(task.Id);

        // Check if task exists
        if (!await _redis.KeyExistsAsync(key))
        {
            return false;
        }

        // If task is being marked as completed, update timestamp and counter
        var existingJson = await _redis.StringGetAsync(key);
        if (!existingJson.IsNullOrEmpty)
        {
            var existingTask = JsonSerializer.Deserialize<TaskItem>(existingJson!, JsonOptions);

            if (existingTask != null && !existingTask.IsCompleted && task.IsCompleted)
            {
                task.CompletedAt = DateTime.UtcNow;
                await _redis.StringIncrementAsync(StatsCompletedKey);
            }
        }

        var json = JsonSerializer.Serialize(task, JsonOptions);
        await _redis.StringSetAsync(key, json);

        _logger.LogInformation("Updated task {TaskId}", task.Id);

        await AddActivityLogAsync(task.Id, $"Task updated (completed: {task.IsCompleted})");

        return true;
    }

    public async Task<bool> DeleteTaskAsync(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Task ID cannot be null or empty", nameof(id));
        }

        var key = GetTaskKey(id);

        // Atomic transaction: delete task + remove from index
        var transaction = _redis.CreateTransaction();

        _ = transaction.KeyDeleteAsync(key);
        _ = transaction.SetRemoveAsync(TaskIdsKey, id);

        bool committed = await transaction.ExecuteAsync();

        if (committed)
        {
            _logger.LogInformation("Deleted task {TaskId}", id);
        }

        return committed;
    }

    public async Task<Dictionary<string, int>> GetStatisticsAsync()
    {
        // Batch fetch counters
        var createdTask = _redis.StringGetAsync(StatsCreatedKey);
        var completedTask = _redis.StringGetAsync(StatsCompletedKey);
        var activeTask = _redis.SetLengthAsync(TaskIdsKey);

        await Task.WhenAll(createdTask, completedTask, activeTask);

        return new Dictionary<string, int>
        {
            ["totalCreated"] = (int)(createdTask.Result.IsNullOrEmpty ? 0 : (long)createdTask.Result),
            ["totalCompleted"] = (int)(completedTask.Result.IsNullOrEmpty ? 0 : (long)completedTask.Result),
            ["activeTasks"] = (int)activeTask.Result
        };
    }

    private static string GetTaskKey(string id) => $"{TaskPrefix}{id}";

    private async Task AddActivityLogAsync(string taskId, string action)
    {
        // Create log key.
        var logKey = $"{TaskPrefix}{taskId}{TaskLogSuffix}";

        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

        // Create the log.
        var entry = $"[{timestamp}] {action}";

        // Add on the left.
        await _redis.ListLeftPushAsync(logKey, entry);

        // Control the list.
        await _redis.ListTrimAsync(logKey, 0, 99);

        _logger.LogInformation("Activity log added for task {TaskId}: {Action}", taskId, action);
    }
}
