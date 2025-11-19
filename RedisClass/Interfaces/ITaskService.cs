using RedisClass.Models;

namespace RedisClass.Interfaces
{
    public interface ITaskService
    {
        Task<TaskItem?> GetTaskAsync(string id);
        Task<IEnumerable<TaskItem>> GetAllTasksAsync();
        Task<List<string>> GetActivityLogAsync(string taskId);
        Task<TaskItem> CreateTaskAsync(TaskItem task);
        Task<bool> UpdateTaskAsync(TaskItem task);
        Task<bool> DeleteTaskAsync(string id);
        Task<Dictionary<string, int>> GetStatisticsAsync();
    }
}
