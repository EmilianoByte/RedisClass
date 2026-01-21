namespace RedisClass.Models.Vins
{
    public class VinCheckResult
    {
        public int TotalRecords { get; set; }
        public int Unchanged { get; set; }
        public int NewRecords { get; set; }
        public int TargaChanged { get; set; }
        public int TelaioReassigned { get; set; }
        public int Errors { get; set; }
        public List<string> ErrorMessages { get; set; } = new();
        public TimeSpan ProcessingTime { get; set; }
    }
}
