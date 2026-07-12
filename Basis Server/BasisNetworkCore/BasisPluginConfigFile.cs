using System;
using System.IO;
using System.Reflection;
using System.Xml.Serialization;

namespace Basis.Network.Core
{
    /// <summary>
    /// Loads a plugin's sidecar configuration ("config/plugins/{id}.xml"):
    /// an XML-serialized settings object whose file self-creates with defaults
    /// on first run, and whose public fields can be overridden by environment
    /// variables of the same name — the same override behavior the core
    /// configuration has, so Docker deployments configure plugins and the
    /// server identically. Plugins get their whole config story from one call:
    /// <c>BasisPluginConfigFile.LoadOrCreate&lt;MySettings&gt;(path)</c>.
    /// </summary>
    public static class BasisPluginConfigFile
    {
        /// <summary>
        /// Reads <typeparamref name="T"/> from <paramref name="path"/>, writing
        /// a defaults file first when none exists, then applies environment
        /// overrides. Never throws: unreadable files fall back to defaults with
        /// a logged error.
        /// </summary>
        public static T LoadOrCreate<T>(string path) where T : class, new()
        {
            T settings = ReadOrWriteDefaults<T>(path);
            ApplyEnvironmentalOverrides(settings);
            return settings;
        }

        private static T ReadOrWriteDefaults<T>(string path) where T : class, new()
        {
            var serializer = new XmlSerializer(typeof(T));
            if (File.Exists(path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    return (T)serializer.Deserialize(stream);
                }
                catch (Exception e)
                {
                    BNL.LogError($"[PluginConfig] Failed to read '{path}', using defaults: {e.Message}");
                    return new T();
                }
            }

            var defaults = new T();
            try
            {
                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                using var stream = File.Create(path);
                serializer.Serialize(stream, defaults);
            }
            catch (Exception e)
            {
                BNL.LogWarning($"[PluginConfig] Could not write default config '{path}': {e.Message}");
            }
            return defaults;
        }

        /// <summary>
        /// Environment variables named after public instance fields override the
        /// file, with the same type handling as the core configuration.
        /// </summary>
        public static void ApplyEnvironmentalOverrides(object settings)
        {
            if (settings == null) return;
            foreach (FieldInfo field in settings.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                string value = Environment.GetEnvironmentVariable(field.Name);
                if (value == null) continue;

                BNL.Log($"[PluginConfig] Applying environmental override {field.Name}");
                if (field.FieldType == typeof(int))
                {
                    if (int.TryParse(value, out int number)) field.SetValue(settings, number);
                    else BNL.LogWarning($"Could not parse '{value}' as int for {field.Name}. Failed override.");
                }
                else if (field.FieldType == typeof(ushort))
                {
                    if (ushort.TryParse(value, out ushort number)) field.SetValue(settings, number);
                    else BNL.LogWarning($"Could not parse '{value}' as ushort for {field.Name}. Failed override.");
                }
                else if (field.FieldType == typeof(float))
                {
                    if (float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float number)) field.SetValue(settings, number);
                    else BNL.LogWarning($"Could not parse '{value}' as float for {field.Name}. Failed override.");
                }
                else if (field.FieldType == typeof(string))
                {
                    field.SetValue(settings, value);
                }
                else if (field.FieldType == typeof(bool))
                {
                    if (bool.TryParse(value, out bool flag)) field.SetValue(settings, flag);
                    else BNL.LogWarning($"Could not parse '{value}' as bool for {field.Name}. Failed override.");
                }
                else
                {
                    BNL.LogWarning($"[PluginConfig] Unsupported field type for override {field.Name}.");
                }
            }
        }
    }
}
