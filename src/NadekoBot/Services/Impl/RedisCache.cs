﻿#nullable disable
using Newtonsoft.Json;
using StackExchange.Redis;
using System.Net;

namespace NadekoBot.Services;

public class RedisCache : IDataCache
{
    public ConnectionMultiplexer Redis { get; }

    public IImageCache LocalImages { get; }
    public ILocalDataCache LocalData { get; }

    private readonly string _redisKey;
    private readonly EndPoint _redisEndpoint;

    private readonly object timelyLock = new();

    public RedisCache(
        ConnectionMultiplexer redis,
        IBotCredentials creds,
        IImageCache imageCache,
        ILocalDataCache dataCache)
    {
        Redis = redis;
        _redisEndpoint = Redis.GetEndPoints().First();
        LocalImages = imageCache;
        LocalData = dataCache;
        _redisKey = creds.RedisKey();
    }

    // things here so far don't need the bot id
    // because it's a good thing if different bots 
    // which are hosted on the same PC
    // can re-use the same image/anime data
    public async Task<(bool Success, byte[] Data)> TryGetImageDataAsync(Uri key)
    {
        var _db = Redis.GetDatabase();
        byte[] x = await _db.StringGetAsync("image_" + key);
        return (x != null, x);
    }

    public Task SetImageDataAsync(Uri key, byte[] data)
    {
        var _db = Redis.GetDatabase();
        return _db.StringSetAsync("image_" + key, data);
    }

    public async Task<(bool Success, string Data)> TryGetAnimeDataAsync(string key)
    {
        var _db = Redis.GetDatabase();
        string x = await _db.StringGetAsync("anime_" + key);
        return (x != null, x);
    }

    public Task SetAnimeDataAsync(string key, string data)
    {
        var _db = Redis.GetDatabase();
        return _db.StringSetAsync("anime_" + key, data, TimeSpan.FromHours(3));
    }

    public async Task<(bool Success, string Data)> TryGetNovelDataAsync(string key)
    {
        var _db = Redis.GetDatabase();
        string x = await _db.StringGetAsync("novel_" + key);
        return (x != null, x);
    }

    public Task SetNovelDataAsync(string key, string data)
    {
        var _db = Redis.GetDatabase();
        return _db.StringSetAsync("novel_" + key, data, TimeSpan.FromHours(3));
    }

    public TimeSpan? AddTimelyClaim(ulong id, int period)
    {
        if (period == 0)
            return null;
        lock (timelyLock)
        {
            var time = TimeSpan.FromHours(period);
            var _db = Redis.GetDatabase();
            if ((bool?)_db.StringGet($"{_redisKey}_timelyclaim_{id}") is null)
            {
                _db.StringSet($"{_redisKey}_timelyclaim_{id}", true, time);
                return null;
            }

            return _db.KeyTimeToLive($"{_redisKey}_timelyclaim_{id}");
        }
    }

    public void RemoveAllTimelyClaims()
    {
        var server = Redis.GetServer(_redisEndpoint);
        var _db = Redis.GetDatabase();
        foreach (var k in server.Keys(pattern: $"{_redisKey}_timelyclaim_*"))
            _db.KeyDelete(k, CommandFlags.FireAndForget);
    }

    public bool TryAddAffinityCooldown(ulong userId, out TimeSpan? time)
    {
        var _db = Redis.GetDatabase();
        time = _db.KeyTimeToLive($"{_redisKey}_affinity_{userId}");
        if (time is null)
        {
            time = TimeSpan.FromMinutes(30);
            _db.StringSet($"{_redisKey}_affinity_{userId}", true, time);
            return true;
        }

        return false;
    }

    public bool TryAddDivorceCooldown(ulong userId, out TimeSpan? time)
    {
        var _db = Redis.GetDatabase();
        time = _db.KeyTimeToLive($"{_redisKey}_divorce_{userId}");
        if (time is null)
        {
            time = TimeSpan.FromHours(6);
            _db.StringSet($"{_redisKey}_divorce_{userId}", true, time);
            return true;
        }

        return false;
    }

    public Task SetStreamDataAsync(string url, string data)
    {
        var _db = Redis.GetDatabase();
        return _db.StringSetAsync($"{_redisKey}_stream_{url}", data, TimeSpan.FromHours(6));
    }

    public bool TryGetStreamData(string url, out string dataStr)
    {
        var _db = Redis.GetDatabase();
        dataStr = _db.StringGet($"{_redisKey}_stream_{url}");

        return !string.IsNullOrWhiteSpace(dataStr);
    }

    public TimeSpan? TryAddRatelimit(ulong id, string name, int expireIn)
    {
        var _db = Redis.GetDatabase();
        if (_db.StringSet($"{_redisKey}_ratelimit_{id}_{name}",
                0, // i don't use the value
                TimeSpan.FromSeconds(expireIn),
                When.NotExists))
            return null;

        return _db.KeyTimeToLive($"{_redisKey}_ratelimit_{id}_{name}");
    }

    public bool TryGetEconomy(out string data)
    {
        var _db = Redis.GetDatabase();
        if ((data = _db.StringGet($"{_redisKey}_economy")) != null) return true;

        return false;
    }

    public void SetEconomy(string data)
    {
        var _db = Redis.GetDatabase();
        _db.StringSet($"{_redisKey}_economy", data, TimeSpan.FromMinutes(3));
    }

    public async Task<TOut> GetOrAddCachedDataAsync<TParam, TOut>(
        string key,
        Func<TParam, Task<TOut>> factory,
        TParam param,
        TimeSpan expiry)
        where TOut : class
    {
        var _db = Redis.GetDatabase();

        var data = await _db.StringGetAsync(key);
        if (!data.HasValue)
        {
            var obj = await factory(param);

            if (obj is null)
                return default;

            await _db.StringSetAsync(key, JsonConvert.SerializeObject(obj), expiry);

            return obj;
        }

        return (TOut)JsonConvert.DeserializeObject(data, typeof(TOut));
    }

    public DateTime GetLastCurrencyDecay()
    {
        var db = Redis.GetDatabase();

        var str = (string)db.StringGet($"{_redisKey}_last_currency_decay");
        if (string.IsNullOrEmpty(str))
            return DateTime.MinValue;

        return JsonConvert.DeserializeObject<DateTime>(str);
    }

    public void SetLastCurrencyDecay()
    {
        var db = Redis.GetDatabase();

        db.StringSet($"{_redisKey}_last_currency_decay", JsonConvert.SerializeObject(DateTime.UtcNow));
    }
}