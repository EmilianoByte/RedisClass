using RedisClass.Enums.Vins;
using RedisClass.Interfaces.Vins;
using RedisClass.Models.Vins;
using StackExchange.Redis;
using System.Diagnostics;

namespace RedisClass.Services;
public class VinServiceTransactional : IVinService
{
    private readonly IDatabase _redis;
    private readonly ILogger<VinServiceTransactional> _logger;

    private const string TelaioPrefix = "vin:telaio:";
    private const string TargaPrefix = "vin:targa:";
    private const string BatchPrefix = "vin:batch:";

    public VinServiceTransactional(IDatabase redis, ILogger<VinServiceTransactional> logger)
    {
        _redis = redis;
        _logger = logger;
    }

    public async Task<VinCheckResult> ProcessBatchAsync(
        IEnumerable<VinRecord> records,
        string? batchId = null)
    {
        var sw = Stopwatch.StartNew();
        var result = new VinCheckResult();

        batchId ??= GenerateBatchId();
        var recordsList = records.ToList();
        result.TotalRecords = recordsList.Count;

        _logger.LogInformation(
            "Processing VIN batch {BatchId} with {Count} records (TRANSACTIONAL)",
            batchId,
            result.TotalRecords);

        try
        {
            // Step 1: Bulk fetch (usa batch per performance, non serve atomicità per reads)
            var contexts = await BulkFetchAndClassifyAsync(recordsList);

            // Step 2: Group by status
            var grouped = contexts.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.ToList());

            // Step 3: Process each group WITH TRANSACTIONS
            if (grouped.ContainsKey(VinRecordStatus.Unchanged))
            {
                result.Unchanged = grouped[VinRecordStatus.Unchanged].Count;
            }

            if (grouped.ContainsKey(VinRecordStatus.New))
            {
                var newRecords = grouped[VinRecordStatus.New];
                await ProcessNewRecordsTransactionalAsync(newRecords, batchId);
                result.NewRecords = newRecords.Count;
            }

            if (grouped.ContainsKey(VinRecordStatus.TargaChanged))
            {
                var changedRecords = grouped[VinRecordStatus.TargaChanged];
                await ProcessTargaChangedTransactionalAsync(changedRecords);
                result.TargaChanged = changedRecords.Count;
            }

            if (grouped.ContainsKey(VinRecordStatus.TelaioReassigned))
            {
                var reassignedRecords = grouped[VinRecordStatus.TelaioReassigned];
                await ProcessTelaioReassignedTransactionalAsync(reassignedRecords);
                result.TelaioReassigned = reassignedRecords.Count;
            }

            sw.Stop();
            result.ProcessingTime = sw.Elapsed;

            _logger.LogInformation(
                "Batch {BatchId} completed in {Ms}ms with ACID guarantees",
                batchId,
                sw.ElapsedMilliseconds);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing VIN batch {BatchId}", batchId);
            result.Errors = 1;
            result.ErrorMessages.Add(ex.Message);
            return result;
        }
    }

    #region Transactional Processors

    /// <summary>
    /// Process new records with transaction (atomic insert).
    /// </summary>
    private async Task ProcessNewRecordsTransactionalAsync(
        List<VinProcessingContext> contexts,
        string batchId)
    {
        _logger.LogInformation("Processing {Count} new VIN records (TRANSACTIONAL)", contexts.Count);

        foreach (var context in contexts)
        {
            var record = context.Record;
            record.BatchId = batchId;

            var success = await ExecuteWithRetryAsync(async () =>
            {
                var tran = _redis.CreateTransaction();

                var telaioKey = GetTelaioKey(record.Telaio);
                var targaKey = GetTargaKey(record.Targa);
                var batchKey = GetBatchKey(batchId);

                // Add conditions: keys must NOT exist (safety check)
                tran.AddCondition(Condition.KeyNotExists(telaioKey));
                tran.AddCondition(Condition.KeyNotExists(targaKey));

                // Atomic operations
                _ = tran.HashSetAsync(telaioKey, new HashEntry[]
                {
                    new("targa", record.Targa),
                    new("cliente", record.Cliente),
                    new("lastModified", record.LastModified.ToString("o")),
                    new("batchId", batchId)
                });

                _ = tran.StringSetAsync(targaKey, record.Telaio);
                _ = tran.SetAddAsync(batchKey, record.Telaio);

                // Execute: all or nothing
                return await tran.ExecuteAsync();
            }, maxRetries: 3);

            if (!success)
            {
                _logger.LogError(
                    "Failed to insert VIN record {Telaio} after retries (race condition)",
                    record.Telaio);
            }
        }

        _logger.LogInformation("Inserted {Count} new VIN records", contexts.Count);
    }

    /// <summary>
    /// Process targa changes with transaction (atomic update + delete old key).
    /// </summary>
    private async Task ProcessTargaChangedTransactionalAsync(
        List<VinProcessingContext> contexts)
    {
        _logger.LogInformation("Processing {Count} targa changes (TRANSACTIONAL)", contexts.Count);

        foreach (var context in contexts)
        {
            var record = context.Record;
            var oldTarga = context.OldTarga!;

            var success = await ExecuteWithRetryAsync(async () =>
            {
                var tran = _redis.CreateTransaction();

                var telaioKey = GetTelaioKey(record.Telaio);
                var oldTargaKey = GetTargaKey(oldTarga);
                var newTargaKey = GetTargaKey(record.Targa);

                // Condition: telaio must exist and have old targa.
                tran.AddCondition(Condition.HashEqual(telaioKey, "targa", oldTarga));

                // Atomic operations
                _ = tran.KeyDeleteAsync(oldTargaKey);
                _ = tran.StringSetAsync(newTargaKey, record.Telaio);
                _ = tran.HashSetAsync(telaioKey,
                [
                    new("targa", record.Targa),
                    new("lastModified", DateTime.UtcNow.ToString("o"))
                ]);

                return await tran.ExecuteAsync();
            }, maxRetries: 3);

            if (success)
            {
                _logger.LogDebug(
                    "Targa change for telaio {Telaio}: {OldTarga} -> {NewTarga}",
                    record.Telaio,
                    oldTarga,
                    record.Targa);
            }
            else
            {
                _logger.LogWarning(
                    "Failed to update targa for {Telaio} (concurrent modification)",
                    record.Telaio);
            }
        }

        _logger.LogInformation("Updated {Count} targa changes", contexts.Count);
    }

    /// <summary>
    /// Process telaio reassignment with transaction (critical operation!).
    /// This is the MOST IMPORTANT use case for transactions.
    /// </summary>
    private async Task ProcessTelaioReassignedTransactionalAsync(
        List<VinProcessingContext> contexts)
    {
        _logger.LogInformation(
            "Processing {Count} telaio reassignments (TRANSACTIONAL - CRITICAL)",
            contexts.Count);

        foreach (var context in contexts)
        {
            var record = context.Record;
            var oldTelaio = context.OldTelaio!;

            var success = await ExecuteWithRetryAsync(async () =>
            {
                var tran = _redis.CreateTransaction();

                var oldTelaioKey = GetTelaioKey(oldTelaio);
                var newTelaioKey = GetTelaioKey(record.Telaio);
                var targaKey = GetTargaKey(record.Targa);

                // Conditions: ensure data integrity
                tran.AddCondition(Condition.StringEqual(targaKey, oldTelaio));

                // Atomic operations: transfer targa from old to new telaio
                _ = tran.HashDeleteAsync(oldTelaioKey, "targa");
                _ = tran.StringSetAsync(targaKey, record.Telaio);
                _ = tran.HashSetAsync(newTelaioKey, new HashEntry[]
                {
                    new("targa", record.Targa),
                    new("cliente", record.Cliente),
                    new("lastModified", DateTime.UtcNow.ToString("o"))
                });

                return await tran.ExecuteAsync();
            }, maxRetries: 5); // More retries for critical operation

            if (success)
            {
                _logger.LogWarning(
                    "Targa {Targa} reassigned from telaio {OldTelaio} to {NewTelaio}",
                    record.Targa,
                    oldTelaio,
                    record.Telaio);
            }
            else
            {
                _logger.LogError(
                    "CRITICAL: Failed to reassign targa {Targa} after retries",
                    record.Targa);
            }
        }

        _logger.LogInformation("Processed {Count} telaio reassignments", contexts.Count);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Execute transaction with retry logic (handles race conditions).
    /// </summary>
    private async Task<bool> ExecuteWithRetryAsync(
        Func<Task<bool>> transactionFunc,
        int maxRetries = 3)
    {
        for (int attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                var success = await transactionFunc();

                if (success)
                    return true;

                // Transaction failed (condition not met), retry
                _logger.LogDebug("Transaction failed (attempt {Attempt}/{Max}), retrying...",
                    attempt + 1, maxRetries);

                await Task.Delay(10 * (attempt + 1)); // Exponential backoff
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Transaction error (attempt {Attempt}/{Max})",
                    attempt + 1, maxRetries);
            }
        }

        return false; // Failed after all retries
    }

    private async Task<List<VinProcessingContext>> BulkFetchAndClassifyAsync(
        List<VinRecord> records)
    {
        // Same as before (batch reads are OK, no need for transaction)
        var batch = _redis.CreateBatch();
        var tasks = new Dictionary<VinRecord, (Task<HashEntry[]>, Task<RedisValue>)>();

        foreach (var record in records)
        {
            var telaioKey = GetTelaioKey(record.Telaio);
            var targaKey = GetTargaKey(record.Targa);

            tasks[record] = (
                batch.HashGetAllAsync(telaioKey),
                batch.StringGetAsync(targaKey)
            );
        }

        batch.Execute();
        await Task.WhenAll(tasks.Values.SelectMany(t => new Task[] { t.Item1, t.Item2 }));

        var contexts = new List<VinProcessingContext>();
        foreach (var (record, (telaioTask, targaTask)) in tasks)
        {
            var telaioHash = await telaioTask;
            var targaValue = await targaTask;
            contexts.Add(ClassifyRecord(record, telaioHash, targaValue));
        }

        return contexts;
    }

    private VinProcessingContext ClassifyRecord(
        VinRecord record,
        HashEntry[] telaioHash,
        RedisValue targaValue)
    {
        // Same logic as before
        var context = new VinProcessingContext { Record = record };

        string? existingTarga = telaioHash.FirstOrDefault(h => h.Name == "targa").Value;
        string? existingTelaio = targaValue.HasValue ? targaValue.ToString() : null;

        context.OldTarga = existingTarga;
        context.OldTelaio = existingTelaio;

        bool telaioExists = telaioHash.Length > 0;
        bool targaExists = targaValue.HasValue;

        if (!telaioExists && !targaExists)
            context.Status = VinRecordStatus.New;
        else if (telaioExists && targaExists &&
                 existingTarga == record.Targa &&
                 existingTelaio == record.Telaio)
            context.Status = VinRecordStatus.Unchanged;
        else if (telaioExists && existingTarga != record.Targa)
            context.Status = VinRecordStatus.TargaChanged;
        else if (targaExists && existingTelaio != record.Telaio)
            context.Status = VinRecordStatus.TelaioReassigned;
        else
            context.Status = VinRecordStatus.New;

        return context;
    }

    private static string GetTelaioKey(string telaio) => $"{TelaioPrefix}{telaio}";
    private static string GetTargaKey(string targa) => $"{TargaPrefix}{targa}";
    private static string GetBatchKey(string batchId) => $"{BatchPrefix}{batchId}";
    private static string GenerateBatchId() =>
        $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

    public Task<VinRecord?> GetByTelaioAsync(string telaio) => throw new NotImplementedException();
    public Task<VinRecord?> GetByTargaAsync(string targa) => throw new NotImplementedException();
    public Task ClearAllAsync() => throw new NotImplementedException();

    #endregion
}



//OLD CODE:
//using RedisClass.Enums.Vins;
//using RedisClass.Interfaces.Vins;
//using RedisClass.Models.Vins;
//using StackExchange.Redis;
//using System.Diagnostics;

//namespace RedisClass.Services;

//public class VinService(IDatabase redis, ILogger<VinService> logger) : IVinService
//{
//    private readonly IDatabase _redis = redis;
//    private readonly ILogger<VinService> _logger = logger;

//    // Redis key prefixes.
//    private const string TelaioPrefix = "vin:telaio:";
//    private const string TargaPrefix = "vin:targa:";
//    private const string BatchPrefix = "vin:batch:";

//    #region Public API

//    /// <summary>
//    /// Process a batch of VIN records from external source.
//    /// Used bulk operations.
//    /// </summary>
//    public async Task<VinCheckResult> ProcessBatchAsync(
//        IEnumerable<VinRecord> records,
//        string? batchId = null)
//    {
//        var sw = Stopwatch.StartNew();
//        var result = new VinCheckResult();

//        batchId ??= GenerateBatchId();
//        var recordsList = records.ToList();
//        result.TotalRecords = recordsList.Count;

//        _logger.LogInformation(
//            "Processing VIN batch {BatchId} with {Count} records",
//            batchId,
//            result.TotalRecords);

//        try
//        {
//            // Step 1: Bulk fetch existing data from Redis.
//            var contexts = await BulkFetchAndClassifyAsync(recordsList);

//            // Step 2: Group by status for efficient processing.
//            var grouped = contexts.GroupBy(c => c.Status).ToDictionary(g => g.Key, g => g.ToList());

//            // Step 3: Process each group.
//            if (grouped.ContainsKey(VinRecordStatus.Unchanged))
//            {
//                result.Unchanged = grouped[VinRecordStatus.Unchanged].Count;
//                _logger.LogDebug("{Count} records unchanged", result.Unchanged);
//            }

//            if (grouped.ContainsKey(VinRecordStatus.New))
//            {
//                var newRecords = grouped[VinRecordStatus.New];
//                await ProcessNewRecordsAsync(newRecords, batchId);
//                result.NewRecords = newRecords.Count;
//            }

//            if (grouped.ContainsKey(VinRecordStatus.TargaChanged))
//            {
//                var changedRecords = grouped[VinRecordStatus.TargaChanged];
//                await ProcessTargaChangedAsync(changedRecords);
//                result.TargaChanged = changedRecords.Count;
//            }

//            if (grouped.ContainsKey(VinRecordStatus.TelaioReassigned))
//            {
//                var reassignedRecords = grouped[VinRecordStatus.TelaioReassigned];
//                await ProcessTelaioReassignedAsync(reassignedRecords);
//                result.TelaioReassigned = reassignedRecords.Count;
//            }

//            sw.Stop();
//            result.ProcessingTime = sw.Elapsed;

//            _logger.LogInformation(
//                "Batch {BatchId} completed in {Ms}ms: {New} new, {Changed} targa changed, {Reassigned} telaio reassigned, {Unchanged} unchanged",
//                batchId,
//                sw.ElapsedMilliseconds,
//                result.NewRecords,
//                result.TargaChanged,
//                result.TelaioReassigned,
//                result.Unchanged);

//            return result;
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Error processing VIN batch {BatchId}", batchId);
//            result.Errors = 1;
//            result.ErrorMessages.Add(ex.Message);
//            return result;
//        }
//    }

//    /// <summary>
//    /// Get VIN record by chassis number (telaio).
//    /// </summary>
//    public async Task<VinRecord?> GetByTelaioAsync(string telaio)
//    {
//        if (string.IsNullOrWhiteSpace(telaio))
//            throw new ArgumentException("Telaio cannot be empty", nameof(telaio));

//        var key = GetTelaioKey(telaio);
//        var fields = await _redis.HashGetAllAsync(key);

//        if (fields.Length == 0)
//            return null;

//        return ParseVinRecordFromHash(telaio, fields);
//    }

//    /// <summary>
//    /// Get VIN record by license plate (targa).
//    /// </summary>
//    public async Task<VinRecord?> GetByTargaAsync(string targa)
//    {
//        if (string.IsNullOrWhiteSpace(targa))
//            throw new ArgumentException("Targa cannot be empty", nameof(targa));

//        // Lookup targa -> telaio
//        var targaKey = GetTargaKey(targa);
//        var telaio = await _redis.StringGetAsync(targaKey);

//        if (telaio.IsNullOrEmpty)
//            return null;

//        // Fetch full record from telaio hash
//        return await GetByTelaioAsync(telaio.ToString());
//    }

//    /// <summary>
//    /// Clear all VIN data from Redis (use with caution in production).
//    /// </summary>
//    public async Task ClearAllAsync()
//    {
//        _logger.LogWarning("Clearing all VIN data from Redis");

//        // Note: In production, you'd want to use SCAN instead of KEYS
//        // This is a simplified version for demonstration
//        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());

//        var telaioKeys = server.Keys(pattern: $"{TelaioPrefix}*").ToArray();
//        var targaKeys = server.Keys(pattern: $"{TargaPrefix}*").ToArray();
//        var batchKeys = server.Keys(pattern: $"{BatchPrefix}*").ToArray();

//        var allKeys = telaioKeys.Concat(targaKeys).Concat(batchKeys).ToArray();

//        if (allKeys.Length > 0)
//        {
//            await _redis.KeyDeleteAsync(allKeys);
//            _logger.LogInformation("Deleted {Count} VIN keys from Redis", allKeys.Length);
//        }
//    }

//    #endregion

//    #region Bulk Processing Logic

//    /// <summary>
//    /// Bulk fetch existing data from Redis and classify each record's status.
//    /// Uses pipeline for performance.
//    /// </summary>
//    private async Task<List<VinProcessingContext>> BulkFetchAndClassifyAsync(
//        List<VinRecord> records)
//    {
//        _logger.LogDebug("Bulk fetching {Count} records from Redis", records.Count);

//        // Build batch of Redis operations (pipeline).
//        var batch = _redis.CreateBatch();
//        var tasks = new Dictionary<VinRecord, (Task<HashEntry[]> telaioTask, Task<RedisValue> targaTask)>();

//        foreach (var record in records)
//        {
//            var telaioKey = GetTelaioKey(record.Telaio);
//            var targaKey = GetTargaKey(record.Targa);

//            var telaioTask = batch.HashGetAllAsync(telaioKey);
//            var targaTask = batch.StringGetAsync(targaKey);

//            tasks[record] = (telaioTask, targaTask);
//        }

//        // Execute all operations in one network round-trip.
//        batch.Execute();

//        // Wait for all results.
//        await Task.WhenAll(tasks.Values.SelectMany(t => new Task[] { t.telaioTask, t.targaTask }));

//        // Classify each record based on Redis state.
//        var contexts = new List<VinProcessingContext>();

//        foreach (var (record, (telaioTask, targaTask)) in tasks)
//        {
//            var telaioHash = await telaioTask;
//            var targaValue = await targaTask;

//            var context = ClassifyRecord(record, telaioHash, targaValue);
//            contexts.Add(context);
//        }

//        _logger.LogDebug(
//            "Classification complete: {New} new, {Changed} changed, {Reassigned} reassigned, {Unchanged} unchanged",
//            contexts.Count(c => c.Status == VinRecordStatus.New),
//            contexts.Count(c => c.Status == VinRecordStatus.TargaChanged),
//            contexts.Count(c => c.Status == VinRecordStatus.TelaioReassigned),
//            contexts.Count(c => c.Status == VinRecordStatus.Unchanged));

//        return contexts;
//    }

//    /// <summary>
//    /// Classify a single record based on Redis state.
//    /// </summary>
//    private VinProcessingContext ClassifyRecord(
//        VinRecord record,
//        HashEntry[] telaioHash,
//        RedisValue targaValue)
//    {
//        var context = new VinProcessingContext { Record = record };

//        // Parse existing data.
//        string? existingTarga = telaioHash.FirstOrDefault(h => h.Name == "targa").Value;
//        string? existingTelaio = targaValue.HasValue ? targaValue.ToString() : null;

//        context.OldTarga = existingTarga;
//        context.OldTelaio = existingTelaio;

//        // Decision matrix.
//        bool telaioExists = telaioHash.Length > 0;
//        bool targaExists = targaValue.HasValue;

//        if (!telaioExists && !targaExists)
//        {
//            // Neither exists -> NEW record.
//            context.Status = VinRecordStatus.New;
//        }
//        else if (telaioExists && targaExists &&
//                 existingTarga == record.Targa &&
//                 existingTelaio == record.Telaio)
//        {
//            // Both match -> UNCHANGED.
//            context.Status = VinRecordStatus.Unchanged;
//        }
//        else if (telaioExists && existingTarga != record.Targa)
//        {
//            // Telaio exists but targa changed -> TARGA CHANGED.
//            context.Status = VinRecordStatus.TargaChanged;
//        }
//        else if (targaExists && existingTelaio != record.Telaio)
//        {
//            // Targa exists but assigned to different telaio -> TELAIO REASSIGNED.
//            context.Status = VinRecordStatus.TelaioReassigned;
//        }
//        else
//        {
//            // Edge case: partial data exists, treat as new.
//            context.Status = VinRecordStatus.New;
//        }

//        return context;
//    }

//    #endregion

//    #region Status-Specific Processors

//    /// <summary>
//    /// Process new VIN records (insert to SQL + Redis).
//    /// </summary>
//    private async Task ProcessNewRecordsAsync(
//        List<VinProcessingContext> contexts,
//        string batchId)
//    {
//        _logger.LogInformation("Processing {Count} new VIN records", contexts.Count);

//        // TODO: Bulk INSERT to SQL database
//        // await _sqlRepository.BulkInsertAsync(contexts.Select(c => c.Record));

//        // Bulk write to Redis using pipeline
//        var batch = _redis.CreateBatch();
//        var tasks = new List<Task>();

//        foreach (var context in contexts)
//        {
//            var record = context.Record;
//            record.BatchId = batchId;

//            // Write telaio hash
//            var telaioKey = GetTelaioKey(record.Telaio);
//            tasks.Add(batch.HashSetAsync(telaioKey, new HashEntry[]
//            {
//                new("targa", record.Targa),
//                new("cliente", record.Cliente),
//                new("lastModified", record.LastModified.ToString("o")),
//                new("batchId", batchId)
//            }));

//            // Write targa reverse lookup
//            var targaKey = GetTargaKey(record.Targa);
//            tasks.Add(batch.StringSetAsync(targaKey, record.Telaio));

//            // Add to batch tracking set
//            var batchKey = GetBatchKey(batchId);
//            tasks.Add(batch.SetAddAsync(batchKey, record.Telaio));
//        }

//        batch.Execute();
//        await Task.WhenAll(tasks);

//        _logger.LogInformation("Inserted {Count} new VIN records", contexts.Count);
//    }

//    /// <summary>
//    /// Process VIN records where targa changed (update SQL + Redis).
//    /// </summary>
//    private async Task ProcessTargaChangedAsync(List<VinProcessingContext> contexts)
//    {
//        _logger.LogInformation("Processing {Count} targa changes", contexts.Count);

//        // TODO: Bulk UPDATE to SQL database
//        // await _sqlRepository.BulkUpdateAsync(contexts.Select(c => c.Record));

//        // Bulk update Redis using pipeline
//        var batch = _redis.CreateBatch();
//        var tasks = new List<Task>();

//        foreach (var context in contexts)
//        {
//            var record = context.Record;
//            var oldTarga = context.OldTarga!;

//            _logger.LogDebug(
//                "Targa change for telaio {Telaio}: {OldTarga} -> {NewTarga}",
//                record.Telaio,
//                oldTarga,
//                record.Targa);

//            // Delete old targa key
//            var oldTargaKey = GetTargaKey(oldTarga);
//            tasks.Add(batch.KeyDeleteAsync(oldTargaKey));

//            // Create new targa key
//            var newTargaKey = GetTargaKey(record.Targa);
//            tasks.Add(batch.StringSetAsync(newTargaKey, record.Telaio));

//            // Update telaio hash with new targa
//            var telaioKey = GetTelaioKey(record.Telaio);
//            tasks.Add(batch.HashSetAsync(telaioKey, new HashEntry[]
//            {
//                new("targa", record.Targa),
//                new("lastModified", DateTime.UtcNow.ToString("o"))
//            }));
//        }

//        batch.Execute();
//        await Task.WhenAll(tasks);

//        _logger.LogInformation("Updated {Count} targa changes", contexts.Count);
//    }

//    /// <summary>
//    /// Process VIN records where targa was reassigned to different telaio.
//    /// </summary>
//    private async Task ProcessTelaioReassignedAsync(List<VinProcessingContext> contexts)
//    {
//        _logger.LogInformation("Processing {Count} telaio reassignments", contexts.Count);

//        // TODO: Complex SQL logic - may need to archive old association
//        // await _sqlRepository.HandleReassignmentAsync(contexts.Select(c => c.Record));

//        // Bulk update Redis using pipeline
//        var batch = _redis.CreateBatch();
//        var tasks = new List<Task>();

//        foreach (var context in contexts)
//        {
//            var record = context.Record;
//            var oldTelaio = context.OldTelaio!;

//            _logger.LogWarning(
//                "Targa {Targa} reassigned from telaio {OldTelaio} to {NewTelaio}",
//                record.Targa,
//                oldTelaio,
//                record.Telaio);

//            // Remove targa from old telaio hash (if it exists)
//            var oldTelaioKey = GetTelaioKey(oldTelaio);
//            tasks.Add(batch.HashDeleteAsync(oldTelaioKey, "targa"));

//            // Update targa key to point to new telaio
//            var targaKey = GetTargaKey(record.Targa);
//            tasks.Add(batch.StringSetAsync(targaKey, record.Telaio));

//            // Create/update new telaio hash
//            var newTelaioKey = GetTelaioKey(record.Telaio);
//            tasks.Add(batch.HashSetAsync(newTelaioKey, new HashEntry[]
//            {
//                new("targa", record.Targa),
//                new("cliente", record.Cliente),
//                new("lastModified", DateTime.UtcNow.ToString("o"))
//            }));
//        }

//        batch.Execute();
//        await Task.WhenAll(tasks);

//        _logger.LogInformation("Processed {Count} telaio reassignments", contexts.Count);
//    }

//    #endregion

//    #region Helper Methods

//    private static string GetTelaioKey(string telaio) => $"{TelaioPrefix}{telaio}";
//    private static string GetTargaKey(string targa) => $"{TargaPrefix}{targa}";
//    private static string GetBatchKey(string batchId) => $"{BatchPrefix}{batchId}";

//    private static string GenerateBatchId() =>
//        $"{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}";

//    private VinRecord ParseVinRecordFromHash(string telaio, HashEntry[] fields)
//    {
//        var dict = fields.ToDictionary(h => h.Name.ToString(), h => h.Value.ToString());

//        return new VinRecord
//        {
//            Telaio = telaio,
//            Targa = dict.GetValueOrDefault("targa", ""),
//            Cliente = dict.GetValueOrDefault("cliente", ""),
//            LastModified = DateTime.TryParse(
//                dict.GetValueOrDefault("lastModified", ""),
//                out var dt) ? dt : DateTime.MinValue,
//            BatchId = dict.GetValueOrDefault("batchId", null)
//        };
//    }

//    #endregion
//}
