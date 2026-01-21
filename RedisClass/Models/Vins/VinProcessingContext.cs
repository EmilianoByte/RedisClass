using RedisClass.Enums.Vins;

namespace RedisClass.Models.Vins
{
    public class VinProcessingContext
    {
        public VinRecord Record { get; set; } = null!;
        public VinRecordStatus Status { get; set; }
        public string? OldTarga { get; set; }
        public string? OldTelaio { get; set; }
    }
}
