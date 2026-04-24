using System;
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
                var config = JsonSerializer.Deserialize(json, CoreJsonContext.Default.AppConfig);
                if (config != null)
                {
                    ApplyStrategyPriorityMigration(config);
                }
                return config ?? new AppConfig();
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

    // Auto-upgrade a stored StrategyPriority list from an older default to the current default,
    // but ONLY if the user hadn't customized it. See Models/StrategyDefaults.cs for version
    // semantics. Runs inline during Load — no separate user action required.
    private void ApplyStrategyPriorityMigration(AppConfig config)
    {
        if (StrategyDefaults.TryMigratePriorityList(
                config.StrategyPriority,
                config.StrategyPriorityDefaultsVersion,
                out var migrated))
        {
            _logger?.Info("[Settings] Strategy priority defaults migrated v"
                + config.StrategyPriorityDefaultsVersion + " → v" + StrategyDefaults.CurrentVersion
                + " (your list matched the old default, so it's been updated; customized lists are preserved).");
            config.StrategyPriority = migrated;
        }
        // Always stamp the current version forward so we don't re-check the migration next load.
        config.StrategyPriorityDefaultsVersion = StrategyDefaults.CurrentVersion;

        // Defensive fill: older configs predate YouTubeComboClientOrder entirely. Seed with
        // current default so yt-combo can run the new broad client list without the user editing
        // JSON by hand.
        if (config.YouTubeComboClientOrder == null || config.YouTubeComboClientOrder.Count == 0)
        {
            config.YouTubeComboClientOrder = new System.Collections.Generic.List<string>(
                StrategyDefaults.YouTubeComboClientOrderDefault);
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
