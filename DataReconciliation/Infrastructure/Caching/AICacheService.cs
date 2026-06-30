using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace DataReconciliation.Infrastructure.Caching
{
    public class AICacheService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<AICacheService> _logger;
        private readonly TimeSpan _defaultExpiry = TimeSpan.FromHours(24);
        private const double MinConfidenceToCache = 0.90;

        public AICacheService(IMemoryCache cache, ILogger<AICacheService> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public bool TryGetResponse(string cacheKey, out AIInferenceResponse? response)
        {
            var hit = _cache.TryGetValue(cacheKey, out response);
            if (hit)
                _logger.LogInformation("AI cache HIT for key={CacheKey}", cacheKey);
            else
                _logger.LogInformation("AI cache MISS for key={CacheKey}", cacheKey);
            return hit;
        }

        public void SetResponse(string cacheKey, AIInferenceResponse response)
        {
            if (response.ConfidenceScore < MinConfidenceToCache)
            {
                _logger.LogWarning("Response not cached due to low confidence: Score={Score} Key={CacheKey}", response.ConfidenceScore, cacheKey);
                return;
            }

            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = _defaultExpiry,
                SlidingExpiration = TimeSpan.FromHours(1)
            };
            _cache.Set(cacheKey, response, options);
            _logger.LogInformation("AI response cached: Key={CacheKey} Confidence={Confidence}", cacheKey, response.ConfidenceScore);
        }

        public static string GenerateCacheKey(string sourceSchemaHash, string targetSchemaHash, string? rulesHash = null)
        {
            var raw = $"{sourceSchemaHash}:{targetSchemaHash}:{rulesHash ?? "none"}";
            using var sha = SHA256.Create();
            var bytes = Encoding.UTF8.GetBytes(raw);
            return $"ai_cache_{Convert.ToHexString(sha.ComputeHash(bytes))[..16]}";
        }
    }
}
