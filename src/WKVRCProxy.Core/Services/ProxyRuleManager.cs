using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WKVRCProxy.Core.Logging;
using WKVRCProxy.Core.Models;

namespace WKVRCProxy.Core.Services;

public class ProxyRuleManager : IProxyModule
{
    public string Name => "ProxyRuleManager";
    
    private Logger? _logger;
    private ProxyRulesConfig _config = new();
    private string _rulesPath = "";

    public Task InitializeAsync(IModuleContext context)
    {
        _logger = context.Logger;

        _rulesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "proxy-rules.json");

        if (File.Exists(_rulesPath))
        {
            try
            {
                string json = File.ReadAllText(_rulesPath);
                var loaded = JsonSerializer.Deserialize<ProxyRulesConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (loaded != null) {
                    _config = loaded;
                    // Ensure the dictionary is case-insensitive
                    var newDict = new System.Collections.Generic.Dictionary<string, ProxyRule>(StringComparer.OrdinalIgnoreCase);
                    foreach(var kvp in _config.Domains) newDict[kvp.Key] = kvp.Value;
                    _config.Domains = newDict;
                }
                _logger.Debug("Loaded proxy-rules.json");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to load proxy rules: " + ex.Message);
            }
        }
        else
        {
            // Create default
            _config.Domains["youtube.com"] = new ProxyRule {
                ForwardReferer = "always",
                OverrideUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
                // PO tokens are now obtained inside yt-dlp via the bgutil plugin at resolution
                // time, not at relay time. No per-rule flag is needed here.
            };
            _config.Domains["googlevideo.com"] = new ProxyRule {
                ForwardReferer = "always",
                OverrideUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                UseCurlImpersonate = true
            };
            
            try
            {
                string json = JsonSerializer.Serialize(_config, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_rulesPath, json);
                _logger.Debug("Created default proxy-rules.json");
            }
            catch (Exception ex)
            {
                _logger.Error("Failed to write proxy rules: " + ex.Message);
            }
        }

        return Task.CompletedTask;
    }

    public ProxyRule GetRuleForDomain(string domain)
    {
        // Try exact match and fallback to suffix
        if (_config.Domains.TryGetValue(domain, out var exactRule)) return exactRule;

        foreach (var kvp in _config.Domains)
        {
            if (domain.EndsWith("." + kvp.Key, StringComparison.OrdinalIgnoreCase) || domain.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return _config.Default;
    }

    public void Shutdown() { }
}
