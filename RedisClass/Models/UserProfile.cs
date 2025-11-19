namespace RedisClass.Models
{
    public class UserProfile
    {
        public string UserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public int TasksCreated { get; set; }
        public int TasksCompleted { get; set; }
        public DateTime LastActive { get; set; }
    }
}