using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
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
                    ApplyWarpAsStrategyMigration(config, json);
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

    // One-shot migration: WARP used to be a global on/off (`enableWarp` config flag) that gated
    // whether the WARP strategy variants were even added to the cold race. It's now a regular pair
    // of strategies (`tier1:warp+default`, `tier1:warp+vrchat-ua`) that the user toggles in the
    // Strategy Panel. To preserve old behavior, this migration reads the raw JSON for the now-
    // removed `enableWarp` key — if it was absent or false, we add the warp strategies to
    // DisabledTiers so they stay off until the user explicitly enables them. Users who had
    // `enableWarp: true` get to keep their opt-in (we don't add to DisabledTiers in that case).
    private void ApplyWarpAsStrategyMigration(AppConfig config, string rawJson)
    {
        bool warpWasOn;
        try
        {
            var root = JsonNode.Parse(rawJson)?.AsObject();
            // If the field is missing entirely (new config) or false, treat as "warp was off".
            warpWasOn = root != null
                && root.TryGetPropertyValue("enableWarp", out var node)
                && node?.GetValue<bool>() == true;
        }
        catch
        {
            // Malformed JSON? The outer load will hit its own catch — just bail safely here.
            return;
        }

        if (warpWasOn) return;

        string[] warpStrategies = { "tier1:warp+default", "tier1:warp+vrchat-ua" };
        bool changed = false;
        foreach (var s in warpStrategies)
        {
            if (!config.DisabledTiers.Any(t => string.Equals(t, s, StringComparison.OrdinalIgnoreCase)))
            {
                config.DisabledTiers.Add(s);
                changed = true;
            }
        }
        if (changed)
        {
            _logger?.Info("[Settings] Migrated WARP from a global toggle to per-strategy entries — added "
                + string.Join(", ", warpStrategies) + " to DisabledTiers (you can re-enable each in the Strategy Panel).");
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
