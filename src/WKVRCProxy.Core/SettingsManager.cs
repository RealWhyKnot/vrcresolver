using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core;

public class SettingsManager
{
    private readonly string _filePath;
    private AppConfig _config;
    private readonly object _lock = new object();

    // Injected after construction to avoid circular dependency (Logger depends on SettingsManager).
    // Call SetLogger() once the Logger is created.
    private Logger? _logger;

    public AppConfig Config => _config;

    public SettingsManager(string baseDir)
    {
        _filePath = Path.Combine(baseDir, "app_config.json");
        _config = Load();
    }

    public void SetLogger(Logger logger)
    {
        _logger = logger;
    }

    private AppConfig Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                var cfg = new AppConfig();
#if DEBUG
                cfg.DebugMode = true;
#endif
                return cfg;
            }

            try
            {
                string json = File.ReadAllText(_filePath);

                // Pre-deserialize peek: was the userOverriddenKeys field present in the raw JSON?
                // System.Text.Json returns an empty HashSet for both "present and empty" and
                // "absent" once deserialized, but those mean different things — the latter is a
                // pre-override-tracking config whose override state must be inferred from its
                // field values (see InferLegacyOverrides).
                bool hadOverrideField = JsonHasProperty(json, "userOverriddenKeys");

                var config = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppConfig);
                if (config == null) return new AppConfig();

                if (!hadOverrideField)
                {
                    InferLegacyOverrides(config);
                }
                SyncDefaultsForNonOverriddenFields(config);
                return config;
            }
            catch (Exception ex)
            {
                // Logger not yet available at construction time — write directly to a fallback file
                try
                {
                    string errFile = Path.Combine(Path.GetDirectoryName(_filePath)!, "settings_load_error.log");
                    File.WriteAllText(errFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Failed to load app_config.json: {ex}\n");
                }
                catch { }
                return new AppConfig();
            }
        }
    }

    private static bool JsonHasProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out _);
        }
        catch
        {
            return false;
        }
    }

    // For configs saved before the override-tracking schema landed, decide whether each
    // default-tracked field's stored value matches a known shipped default (→ not overridden;
    // user gets future default updates) or a customization (→ overridden; preserved verbatim).
    private void InferLegacyOverrides(AppConfig config)
    {
        foreach (var (jsonKey, matcher) in DefaultTrackedFields.LegacyMatchers)
        {
            if (matcher(config)) continue; // Looks like a known default — leave key out of overrides
            config.UserOverriddenKeys.Add(jsonKey);
            _logger?.Info("[Settings] Inferred user customization on '" + jsonKey + "' from legacy config — preserving verbatim, future default updates will skip it.");
        }
    }

    // For each default-tracked field NOT in UserOverriddenKeys, copy the current code default in.
    // Net effect: editing a default constant in source flows out to every user who hasn't
    // customized that field, with no version bump or per-field migration code.
    private void SyncDefaultsForNonOverriddenFields(AppConfig config)
    {
        foreach (var (jsonKey, resetter) in DefaultTrackedFields.Resetters)
        {
            if (config.UserOverriddenKeys.Contains(jsonKey)) continue;
            resetter(config);
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            try
            {
                string json = JsonSerializer.Serialize(_config, CoreJsonContext.Default.AppConfig);
                File.WriteAllText(_filePath, json);
            }
            catch (Exception ex)
            {
                _logger?.Error("Failed to save settings: " + ex.Message, ex);
            }
        }
    }
}
