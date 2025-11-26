namespace RedisClass.Models
{
    public class LeaderboardEntry
    {
        public int Rank { get; set; }
        public string PlayerId { get; set; } = string.Empty;
        public double Score { get; set; }
    }
}
