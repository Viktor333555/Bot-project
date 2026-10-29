using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace Gamebot
{

public class Databasecommuncation
{
    private static readonly HttpClient client = new HttpClient();
    private const string FIREBASE_URL = "https://civbot-954eb-default-rtdb.europe-west1.firebasedatabase.app";
    private const string FirebaseSecretSettingName = "FirebaseSecret";
    private const string FirebaseSecretEnvironmentVariable = "FIREBASE_SECRET";
    private static string? _botId = null;
    private static string? _loadedSecret;
    private static readonly Lazy<string> LoadedFirebaseSecret = new Lazy<string>(LoadFirebaseSecret);

    // Appends ?auth= using the database secret from settings.txt, or from
    // the FIREBASE_SECRET environment variable when the file has no value.
    private static string AddAuth(string url)
    {
        return $"{url}?auth={GetFirebaseSecret()}";
    }

    private static string GetFirebaseSecret() => LoadedFirebaseSecret.Value;

    private static string LoadFirebaseSecret()
    {
        string? secret = ResolveFirebaseSecret();
        _loadedSecret = secret;
        if (string.IsNullOrEmpty(secret))
        {
            Console.WriteLine("⚠ Firebase secret not configured. Add FirebaseSecret to settings.txt or set the FIREBASE_SECRET environment variable.");
            return "";
        }
        return secret;
    }

    private static string? ResolveFirebaseSecret()
    {
        string? fromSettings = ReadFirebaseSecretFromSettings();
        if (!string.IsNullOrWhiteSpace(fromSettings))
            return fromSettings;

        string? fromEnvironment = Environment.GetEnvironmentVariable(FirebaseSecretEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return fromEnvironment.Trim();

        return null;
    }

    // Same settings.txt location and key:::"value" format as Settings.
    private static string? ReadFirebaseSecretFromSettings()
    {
        try
        {
            string path = Settings.filepath_settings;
            if (!File.Exists(path))
                return null;

            string? secret = null;
            foreach (string line in File.ReadAllLines(path))
            {
                (string name, string param) = Settings.ParseIntoNameAndParameter(line);
                if (!string.Equals(name, FirebaseSecretSettingName, StringComparison.Ordinal))
                    continue;
                secret = string.IsNullOrWhiteSpace(param) ? null : param;
            }
            return secret;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Could not read Firebase secret from settings: {Redact(ex.Message)}");
            return null;
        }
    }

    private static string Redact(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return "";

        string redacted = message;
        if (!string.IsNullOrEmpty(_loadedSecret))
            redacted = redacted.Replace(_loadedSecret, "***", StringComparison.Ordinal);

        return System.Text.RegularExpressions.Regex.Replace(
            redacted,
            @"([?&]auth=)[^&\s""']*",
            "$1***");
    }
    
    // Get or create a persistent bot ID
    public static async Task<string> GetOrCreateBotId(string region = "EU-West", string botName = "FFACIV Bot")
    {
        if (_botId != null) return _botId;
        
        string botIdFile = "bot-id.txt";
        
        // Get current machine's hash
        string machineGuid = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", 
            "MachineGuid", 
            Guid.NewGuid().ToString()
        )?.ToString() ?? Guid.NewGuid().ToString();
        
        string hashedId = HashString(machineGuid).Substring(0, 12);
        string expectedBotId = $"bot-{hashedId}";
        
        // Check if we already have a saved bot ID
        if (File.Exists(botIdFile))
        {
            string existingId = File.ReadAllText(botIdFile).Trim();
            if (!string.IsNullOrWhiteSpace(existingId))
            {
                // Validate that the saved ID matches this machine
                if (existingId == expectedBotId)
                {
                    Console.WriteLine($"Using existing bot ID: {existingId}");
                    _botId = existingId;
                    
                    // Update bot info in Firebase with current settings (in case they changed)
                    await UpdateBotInfo(_botId, region, botName);
                    
                    return _botId;
                }
                else
                {
                    Console.WriteLine($"⚠ bot-id.txt contains ID from different machine: {existingId}");
                    Console.WriteLine($"⚠ Generating new bot ID for this machine...");
                }
            }
        }
        
        // Generate new ID for this machine
        Console.WriteLine($"Generated new bot ID: {expectedBotId}");
        
        // Save to file
        File.WriteAllText(botIdFile, expectedBotId);
        _botId = expectedBotId;
        
        // Initialize bot in Firebase
        await InitializeBot(_botId, region, botName);
        
        return _botId;
    }
    
    // Update bot info (name, region) - called on every startup
    private static async Task UpdateBotInfo(string botId, string region, string botName)
    {
        try
        {
            var botInfo = new {
                botName = botName,
                region = region
            };
            
            await UpdateData($"bot-stats/{botId}/info", botInfo);
            Console.WriteLine($"✓ Updated bot info: {botName} ({region})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"⚠ Could not update bot info: {Redact(ex.Message)}");
        }
    }
    
    // Initialize bot in Firebase if it doesn't exist
    private static async Task<bool> InitializeBot(string botId, string region, string botName)
    {
        string existingBot = await GetData($"bot-stats/{botId}/info");
        
        if (existingBot != null && existingBot != "null")
        {
            Console.WriteLine($"Bot {botId} already exists in Firebase, updating info...");
            await UpdateBotInfo(botId, region, botName);
            return false;
        }
        
        Console.WriteLine($"Initializing bot {botId} in Firebase...");
        
        var botInfo = new {
            botId = botId,
            botName = botName,
            region = region,
            registeredAt = DateTime.UtcNow.ToString("o")
        };
        
        await UpdateData($"bot-stats/{botId}/info", botInfo);
        
        var botStats = new {
            amountOfConnected = 0,
            lastGameStart = "",
            lastPing = DateTime.UtcNow.ToString("o"),
            lastRelobby = ""
        };
        
        await UpdateData($"bot-stats/{botId}/stats", botStats);
        Console.WriteLine($"Bot {botId} initialized successfully!");
        return true;
    }
    
    // Helper method to update bot stats
    public static async Task UpdateBotStats(string field, object value)
    {
        try
        {
            string botId = await GetOrCreateBotId();
            var update = new Dictionary<string, object> { { field, value } };
            await UpdateData($"bot-stats/{botId}/stats", update);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating bot stats: {Redact(ex.Message)}");
        }
    }
    
    // Ping the database to show bot is active
    public static async Task PingBot()
    {
        await UpdateBotStats("lastPing", DateTime.UtcNow.ToString("o"));
    }
    
    // Log a game restart
    public static async Task LogGameRestart()
    {
        await UpdateBotStats("lastGameStart", DateTime.UtcNow.ToString("o"));
        Console.WriteLine("Game restart logged to Firebase");
    }
    
    // Log a relobby
    public static async Task LogRelobby()
    {
        await UpdateBotStats("lastRelobby", DateTime.UtcNow.ToString("o"));
        Console.WriteLine("Relobby logged to Firebase");
    }
    
    // Log a player connection
    public static async Task LogPlayerConnection(string playerName)
    {
        try
        {
            string botId = await GetOrCreateBotId();
            
            // Increment connection count
            string statsJson = await GetData($"bot-stats/{botId}/stats");
            int currentCount = 0;
            
            if (statsJson != null && statsJson != "null")
            {
                var stats = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(statsJson);
                if (stats != null && stats.ContainsKey("amountOfConnected"))
                {
                    currentCount = stats["amountOfConnected"].GetInt32();
                }
            }
            
            await UpdateBotStats("amountOfConnected", currentCount + 1);
            
            // Add to history
            long timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string historyPath = $"bot-stats/{botId}/history/{timestamp}";
            await ReplaceData(historyPath, playerName);
            
            Console.WriteLine($"Player connection logged: {playerName}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error logging player connection: {Redact(ex.Message)}");
        }
    }
    
    private static string HashString(string input)
    {
        using (SHA256 sha256 = SHA256.Create())
        {
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
            StringBuilder builder = new StringBuilder();
            foreach (byte b in bytes)
            {
                builder.Append(b.ToString("x2"));
            }
            return builder.ToString();
        }
    }
    
    public static async Task<string> GetData(string path)
    {
        try
        {
            string url = AddAuth($"{FIREBASE_URL}/{path}.json");
            HttpResponseMessage response = await client.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error getting data: {Redact(ex.Message)}");
            return null!;
        }
    }

    public static async Task<string> PostData(string path, object data)
    {
        try
        {
            string url = AddAuth($"{FIREBASE_URL}/{path}.json");
            string jsonData = JsonSerializer.Serialize(data);
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            
            HttpResponseMessage response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error posting data: {Redact(ex.Message)}");
            return null!;
        }
    }

    public static async Task<bool> ReplaceData(string path, object data)
    {
        try
        {
            string url = AddAuth($"{FIREBASE_URL}/{path}.json");
            string jsonData = JsonSerializer.Serialize(data);
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            
            HttpResponseMessage response = await client.PutAsync(url, content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error replacing data: {Redact(ex.Message)}");
            return false;
        }
    }
    
    public static async Task<bool> UpdateData(string path, object data)
    {
        try
        {
            string url = AddAuth($"{FIREBASE_URL}/{path}.json");
            string jsonData = JsonSerializer.Serialize(data);
            var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
            
            HttpResponseMessage response = await client.PatchAsync(url, content);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating data: {Redact(ex.Message)}");
            return false;
        }
    }
    
    public static async Task<bool> DeleteData(string path)
    {
        try
        {
            string url = AddAuth($"{FIREBASE_URL}/{path}.json");
            HttpResponseMessage response = await client.DeleteAsync(url);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting data: {Redact(ex.Message)}");
            return false;
        }
    }
}

}