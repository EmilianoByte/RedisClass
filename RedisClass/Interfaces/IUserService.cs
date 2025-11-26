using RedisClass.Models;

namespace RedisClass.Interfaces
{
    public interface IUserService
    {
        Task<UserProfile> CreateOrUpdateUserAsync(string userId, string name, string email);
        Task<UserProfile?> GetUserAsync(string userId);
        Task IncrementTasksCreatedAsync(string userId);
        Task IncrementTasksCompletedAsync(string userId);
        Task<IEnumerable<UserProfile>> GetAllUsersAsync();
    }
}