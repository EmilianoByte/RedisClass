using RedisClass.Models.Vins;

namespace RedisClass.Interfaces.Vins;

public interface IVinService
{
    Task<VinCheckResult> ProcessBatchAsync(IEnumerable<VinRecord> records,string? batchId = null);
    Task<VinRecord?> GetByTelaioAsync(string telaio);
    Task<VinRecord?> GetByTargaAsync(string targa);
    Task ClearAllAsync();
}
