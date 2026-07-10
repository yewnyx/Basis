using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace BasisNetworkServer.Security
{
    public class BasisAllowList
    {
        private readonly ConcurrentDictionary<string, byte> allowlistedPlayers = new ConcurrentDictionary<string, byte>();
        private readonly string filePath;

        /// <summary>
        /// Fired after the in-memory allowlist changes (add, remove, reload).
        /// Exceptions from subscribers are swallowed so they cannot break the mutation.
        /// </summary>
        public event Action OnChanged;

        public BasisAllowList(string path = "BasisAllowList.txt")
        {
            filePath = path;
            _ = LoadAllowlistAsync(); // Fire and forget
        }

        private async Task LoadAllowlistAsync()
        {
            allowlistedPlayers.Clear();
            if (File.Exists(filePath))
            {
                string[] lines = await File.ReadAllLinesAsync(filePath);
                foreach (string line in lines)
                {
                    string trimmedLine = line.Trim();
                    if (!string.IsNullOrEmpty(trimmedLine))
                    {
                        allowlistedPlayers.TryAdd(trimmedLine, 0);
                    }
                }
            }
        }

        public bool IsAllowed(string playerId) => allowlistedPlayers.ContainsKey(playerId);

        /// <summary>Snapshot of every allowlisted UUID, for external managers.</summary>
        public IReadOnlyList<string> ListAllowed() => new List<string>(allowlistedPlayers.Keys);

        public async Task ReloadAllowlistAsync()
        {
            await LoadAllowlistAsync();
            RaiseChanged();
            Console.WriteLine("Allowlist reloaded.");
        }

        public async Task AddToAllowlistAsync(string playerId)
        {
            if (!allowlistedPlayers.ContainsKey(playerId))
            {
                allowlistedPlayers.TryAdd(playerId, 0);
                RaiseChanged();
                await File.AppendAllTextAsync(filePath, playerId + Environment.NewLine);
                Console.WriteLine($"{playerId} added to allowlist.");
            }
        }

        public async Task RemoveFromAllowlistAsync(string playerId)
        {
            if (allowlistedPlayers.TryRemove(playerId, out _))
            {
                RaiseChanged();
                await SaveAllowlistAsync();
                Console.WriteLine($"{playerId} removed from allowlist.");
            }
        }

        private void RaiseChanged()
        {
            try
            {
                OnChanged?.Invoke();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Allowlist OnChanged subscriber threw: {e}");
            }
        }

        private async Task SaveAllowlistAsync()
        {
            await File.WriteAllLinesAsync(filePath, allowlistedPlayers.Keys);
        }
    }
}
