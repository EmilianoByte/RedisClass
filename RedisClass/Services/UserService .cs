using RedisClass.Models;
using StackExchange.Redis;

namespace TaskAPI.Services
{
    public class UserService(IConnectionMultiplexer redis, ILogger<UserService> logger) : IUserService
    {
        private readonly IDatabase _redis = redis.GetDatabase();
        private readonly ILogger<UserService> _logger = logger;

        private const string UserPrefix = "user:";
        private const string UserIdsKey = "user:ids";

        /// <summary>
        /// Creates or updates a user profile.
        /// </summary>
        public async Task<UserProfile> CreateOrUpdateUserAsync(string userId, string name, string email)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            var key = GetUserKey(userId);

            // Set multiple fields at once.
            await _redis.HashSetAsync(key,
            [
                new HashEntry("name", name),
                new HashEntry("email", email),
                new HashEntry("lastActive", DateTime.UtcNow.ToString("o"))
            ]);

            // Add user ID to index Set if new.
            await _redis.SetAddAsync(UserIdsKey, userId);

            // Initialize counters if they don't exist.
            var countersExist = await _redis.HashExistsAsync(key, "tasksCreated");
            if (!countersExist)
            {
                await _redis.HashSetAsync(key,
                [
                    new HashEntry("tasksCreated", 0),
                    new HashEntry("tasksCompleted", 0)
                ]);
            }

            _logger.LogInformation("User profile created/updated: {UserId}", userId);

            return await GetUserAsync(userId)
                ?? throw new InvalidOperationException("Failed to retrieve user");
        }

        /// <summary>
        /// Retrieves a user profile.
        /// </summary>
        public async Task<UserProfile?> GetUserAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            var key = GetUserKey(userId);

            // Read all fields.
            var fields = await _redis.HashGetAllAsync(key);

            if (fields.Length == 0)
                return null;

            // Convert HashEntry[] to dictionary for easy parsing.
            var dict = fields.ToDictionary(
                x => x.Name.ToString(),
                x => x.Value.ToString()
            );

            return new UserProfile
            {
                UserId = userId,
                Name = dict.GetValueOrDefault("name", ""),
                Email = dict.GetValueOrDefault("email", ""),
                TasksCreated = int.TryParse(dict.GetValueOrDefault("tasksCreated", "0"), out var created)
                    ? created : 0,
                TasksCompleted = int.TryParse(dict.GetValueOrDefault("tasksCompleted", "0"), out var completed)
                    ? completed : 0,
                LastActive = DateTime.TryParse(dict.GetValueOrDefault("lastActive", ""), out var lastActive)
                    ? lastActive : DateTime.MinValue
            };
        }

        /// <summary>
        /// Increments the tasks created counter.
        /// </summary>
        public async Task IncrementTasksCreatedAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            var key = GetUserKey(userId);

            // Atomic increment operation.
            var newCount = await _redis.HashIncrementAsync(key, "tasksCreated", 1);

            // Update last active timestamp.
            await _redis.HashSetAsync(key, "lastActive", DateTime.UtcNow.ToString("o"));

            _logger.LogInformation("User {UserId} tasks created count: {Count}", userId, newCount);
        }

        /// <summary>
        /// Increments the tasks completed counter.
        /// </summary>
        public async Task IncrementTasksCompletedAsync(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be empty", nameof(userId));

            var key = GetUserKey(userId);

            var newCount = await _redis.HashIncrementAsync(key, "tasksCompleted", 1);
            await _redis.HashSetAsync(key, "lastActive", DateTime.UtcNow.ToString("o"));

            _logger.LogInformation("User {UserId} tasks completed count: {Count}", userId, newCount);
        }

        /// <summary>
        /// Retrieves all user profiles.
        /// </summary>
        public async Task<IEnumerable<UserProfile>> GetAllUsersAsync()
        {
            var userIds = await _redis.SetMembersAsync(UserIdsKey);

            var users = new List<UserProfile>();
            foreach (var userId in userIds)
            {
                var user = await GetUserAsync(userId.ToString());
                if (user != null)
                {
                    users.Add(user);
                }
            }

            return users;
        }

        private string GetUserKey(string userId) => $"{UserPrefix}{userId}";
    }
}
