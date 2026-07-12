#if !UNITY_2017_1_OR_NEWER
using System;
using System.Threading.Tasks;

namespace Basis.Network.Server.Redis
{
    /// <summary>
    /// The seven Redis operations the binding actually uses, so the handler
    /// tests against an in-memory fake instead of a live server. This is a
    /// test seam, not a transport abstraction — it is deliberately Redis-shaped
    /// (lists, channels, streams, TTL'd strings) and would not survive a
    /// second transport.
    /// </summary>
    public interface IRedisConnection : IDisposable
    {
        /// <summary>Raised on connect and every reconnect.</summary>
        event Action Connected;

        bool IsConnected { get; }

        /// <summary>LPOP; null when the list is empty.</summary>
        Task<string> ListLeftPopAsync(string key);

        /// <summary>RPUSH followed by EXPIRE — replies must not outlive a dead manager.</summary>
        Task ListRightPushAsync(string key, string value, TimeSpan expiry);

        Task PublishAsync(string channel, string message);

        void Subscribe(string channel, Action<string> onMessage);

        /// <summary>SET, optionally with EX.</summary>
        Task StringSetAsync(string key, string value, TimeSpan? expiry);

        /// <summary>XADD with approximate MAXLEN trimming; fields "type" and "json".</summary>
        Task StreamAddAsync(string key, string type, string json, int maxLength);
    }
}
#endif
