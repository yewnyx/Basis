using UnityEngine;

namespace Basis.ModelPickup
{
    /// <summary>
    /// A switch saved in PlayerPrefs, such as the "receive from others" toggle. Read lazily on first use
    /// (PlayerPrefs is main thread only) and written through, with a save, on every set.
    /// </summary>
    public sealed class BasisModelPersistentBool
    {
        public readonly string Key;

        private readonly bool _defaultValue;
        private bool _value;
        private bool _loaded;

        public BasisModelPersistentBool(string key, bool defaultValue)
        {
            Key = key;
            _defaultValue = defaultValue;
        }

        public bool Value
        {
            get
            {
                if (!_loaded)
                {
                    _value = PlayerPrefs.GetInt(Key, _defaultValue ? 1 : 0) != 0;
                    _loaded = true;
                }
                return _value;
            }
            set
            {
                _value = value;
                _loaded = true;
                PlayerPrefs.SetInt(Key, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }
}
