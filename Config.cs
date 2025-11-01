using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinksReplacerBot;

[method: JsonConstructor]
public class Config(string token)
{
    public static Config Instance { get; private set; }

    /// <summary>
    /// Telegram bot token. Received from <a href="https://t.me/BotFather">BotFather</a>
    /// </summary>
    public string Token { get; } = token;

    public static Config Load(string filepath)
    {
        if (Instance is not null)
            return Instance;

        try
        {
            using var configFile = File.OpenRead(filepath);
            Instance = JsonSerializer.Deserialize<Config>(configFile, CommonOptions.Json);
        }
        catch (Exception)
        {
        }

        return Instance;
    }
}
