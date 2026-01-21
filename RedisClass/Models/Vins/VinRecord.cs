namespace RedisClass.Models.Vins
{
    public class VinRecord
    {
        public string Telaio { get; set; } = string.Empty;
        public string Targa { get; set; } = string.Empty;
        public string Cliente { get; set; } = string.Empty;
        public DateTime LastModified { get; set; } = DateTime.UtcNow;
        public string? BatchId { get; set; }
    }
}
