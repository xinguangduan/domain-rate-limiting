﻿using System;
using StackExchange.Redis;
using System.Collections.Generic;
using System.Threading.Tasks;
using Domain.RateLimiting.Core;

namespace Domain.RateLimiting.Redis
{
    public class RedisLeakyBucketLimiter : RedisRateLimiter
    {
        private const String _luaScript =
            "local key = @key;" +
            "local utcNowTicks = @utcNowTicks; " +
            "local raPerInterval = @refillAmountPerInterval; " +
            "local riInTicks = @refillIntervalInTicks; " +
            "local ttl = @ttl; " +
            "local h = { 'lu', utcNowTicks, 't', 0 }; " +
            "if redis.call('HEXISTS', key, 'lu') == 1 then " +
            "   h = redis.call('HGETALL', key); " +
            "end " +
            "local lrtInTicks = h[2]; " +
            "local ri = math.floor((utcNowTicks - lrtInTicks) / riInTicks); " +
            "local leakage = raPerInterval * ri; " +
            "local nT = h[4] - leakage; " +
            "if nT < 0 then " +
            "   nT = 0; " +
            "end " +
            "h[2] = lrtInTicks + ri * riInTicks; " +
            "h[4] = nT; " +
            "redis.call('HMSET', key, h[1], h[2], h[3], h[4]); " +
            "redis.call('EXPIRE', key, ttl);";

        private static readonly IDictionary<RateLimitUnit, Func<AllowedConsumptionRate, Func<DateTime, string>>> RateLimitTypeCacheKeyFormatMapping =
            new Dictionary<RateLimitUnit, Func<AllowedConsumptionRate, Func<DateTime, string>>>
        {
            {RateLimitUnit.PerSecond, allowedCallrate => _ => RateLimitUnit.PerSecond.ToString()},
            {RateLimitUnit.PerMinute, allowedCallrate => _ => RateLimitUnit.PerMinute.ToString()},
            {RateLimitUnit.PerHour, allowedCallrate => _ => RateLimitUnit.PerHour.ToString()},
            {RateLimitUnit.PerDay, allowedCallrate => _ => RateLimitUnit.PerDay.ToString()},
            {RateLimitUnit.PerCustomPeriod, allowedCallRate => _ =>
                {
                    return $"{allowedCallRate.Period.StartDateTimeUtc.ToString("yyyyMMddHHmmss")}::{allowedCallRate.Period.Duration.TotalSeconds}";
                }
            }
        };


        private readonly LoadedLuaScript _loadedLuaScript;

        public RedisLeakyBucketLimiter(
            string redisEndpoint,
            Action<Exception> onException = null,
            Action<RateLimitingResult> onThrottled = null,
            int connectionTimeoutInMilliseconds = 2000,
            int syncTimeoutInMilliseconds = 1000,
            bool countThrottledRequests = false,
            ICircuitBreaker circuitBreaker = null,
            IClock clock = null,
            Func<Task<IConnectionMultiplexer>> connectToRedisFunc = null) :
            base(redisEndpoint,
                onException,
                onThrottled,
                connectionTimeoutInMilliseconds,
                syncTimeoutInMilliseconds,
                countThrottledRequests,
                circuitBreaker,
                clock,
                connectToRedisFunc)
        {
            var prepared = LuaScript.Prepare(_luaScript);
            _loadedLuaScript = prepared.Load(_redisConnection.GetServer(_redisConnection.GetDatabase().IdentifyEndpoint()));
        }

        protected override Task<long> GetNumberOfRequestsAsync(
            string requestId,
            string method,
            string host,
            string routeTemplate,
            AllowedConsumptionRate allowedConsumptionRate,
            IList<RateLimitCacheKey> cacheKeys,
            ITransaction redisTransaction,
            long utcNowTicks,
            int costPerCall = 1)
        {
            RateLimitCacheKey cacheKey =
              new RateLimitCacheKey(requestId, method, host, routeTemplate, allowedConsumptionRate,
              RateLimitTypeCacheKeyFormatMapping[allowedConsumptionRate.Unit].Invoke(allowedConsumptionRate));

            var cacheKeyString = cacheKey.ToString();
            cacheKeys.Add(cacheKey);

            var ttlInSeconds = allowedConsumptionRate.MaxBurst / allowedConsumptionRate.Limit * (long)allowedConsumptionRate.Unit / TimeSpan.TicksPerSecond + 120;

            var scriptResultTask = redisTransaction.ScriptEvaluateAsync(_loadedLuaScript,
                new
                {
                    key = (RedisKey)cacheKeyString,
                    utcNowTicks = utcNowTicks,
                    refillAmountPerInterval = allowedConsumptionRate.Limit,
                    refillIntervalInTicks = (long)allowedConsumptionRate.Unit,
                    ttl = ttlInSeconds
                });

            return redisTransaction.HashIncrementAsync(cacheKeyString, "t");

        }

        protected override Func<long> GetOldestRequestTimestampInTicksFunc(ITransaction postViolationTransaction, RateLimitCacheKey cacheKey, long utcNowTicks)
        {
            var task = postViolationTransaction.HashGetAsync(cacheKey.ToString(), "lu");

            return () =>
            {
                task.Result.TryParse(out double value);
                return (long)value;
            };
        }

        protected override void UndoUnsuccessfulRequestCount(ITransaction postViolationTransaction, RateLimitCacheKey cacheKey, long utcNowTicks, int costPerCall = 1)
        {
            postViolationTransaction.HashDecrementAsync(cacheKey.ToString(), "t", costPerCall);
        }
    }
}
