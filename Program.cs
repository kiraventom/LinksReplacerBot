using Serilog;
using Serilog.Core;
using Serilog.Events;
using Telegram.Bot;
using System.Timers;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Timer = System.Timers.Timer;

namespace LinksReplacerBot;

public static class Program
{
    private const string PROJECT_NAME = "LinksReplacerBot";
    private const string START_COMMAND = "/start";
    private const string REPLACE_COMMAND = "/replace";

    private static readonly Dictionary<long, List<Message>> _messages = [];
    private static readonly Dictionary<long, int> _waitingForMediaGroupSec = [];

    public static async Task Main()
    {
        var projectDirPath = GetProjectDirPath();
        Directory.CreateDirectory(projectDirPath);

        if (!TryLoadConfig(projectDirPath, out var config))
        {
            Console.WriteLine("Couldn't parse config, exiting");
            return;
        }

        Log.Logger = InitLogger(projectDirPath);
        Log.Information("===== ENTRY POINT =====");

        var client = new TelegramBotClient(config.Token);
        var receiverOptions = new ReceiverOptions()
        {
            AllowedUpdates = [UpdateType.Message]
        };

        client.StartReceiving(OnUpdate, OnError, receiverOptions);

        await Task.Delay(-1);
    }

    private static async Task OnUpdate(ITelegramBotClient client, Update update, CancellationToken ct)
    {
        var message = update.Message;
        var sender = message.From!;

        Log.Information("Received message [{messageId}] with text '{text}' from user [{userId}] '{firstname}'", message.MessageId, message.Text, sender.Id, sender.FirstName);

        // First handle commands, so /start will always work even if listener is active
        var botCommand = message.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.BotCommand);
        if (botCommand is not null)
        {
            var text = message.Text;
            var commandText = text.Substring(botCommand.Offset, botCommand.Length);
            switch (commandText)
            {
                case START_COMMAND:
                    if (_waitingForMediaGroupSec.ContainsKey(sender.Id))
                        _waitingForMediaGroupSec[sender.Id] = -1;

                    _messages.Remove(sender.Id);

                    await client.SendMessage(chatId: sender.Id, text: "Please send the message to replace links in");
                    return;

                case REPLACE_COMMAND:
                    var newLink = text.Substring(REPLACE_COMMAND.Length + 1);
                    Log.Information("Link is {link}", newLink);
                    if (!newLink.StartsWith("https://"))
                    {
                        newLink = "https://" + newLink;
                        Log.Information("Fixed link to {link}", newLink);
                    }
                    else if (!newLink.StartsWith("http://"))
                    {
                        newLink = "http://" + newLink;
                        Log.Information("Fixed link to {link}", newLink);
                    }

                    var isValid = Uri.TryCreate(newLink, UriKind.Absolute, out _);
                    if (!isValid)
                    {
                        await client.SendMessage(chatId: sender.Id, text: "Link is incorrect");
                        return;
                    }

                    if (!_messages.TryGetValue(sender.Id, out var messages))
                    {
                        await client.SendMessage(chatId: sender.Id, text: "You did not send the message to replace links in");
                        return;
                    }

                    _messages.Remove(sender.Id);

                    var messageWithEntities = messages.FirstOrDefault(m => m.Entities != null || m.CaptionEntities != null);
                    var oldEntities = messageWithEntities.Entities ?? messageWithEntities.CaptionEntities ?? Enumerable.Empty<MessageEntity>();
                    var newEntities = new List<MessageEntity>();
                    foreach (var entity in oldEntities)
                    {
                        if (entity.Type == MessageEntityType.TextLink)
                        {
                            var newEntity = new MessageEntity()
                            {
                                Url = newLink,
                                Offset = entity.Offset,
                                Length = entity.Length,
                                Type = MessageEntityType.TextLink
                            };

                            newEntities.Add(newEntity);
                            Log.Information("Replaced text link {link} at [{offset}:{length}] with {link}", entity.Url, entity.Offset, entity.Length, newLink);
                        }
                        else
                        {
                            newEntities.Add(entity);
                            Log.Information("Added entity {type} at [{offset}:{length}] as is", Enum.GetName<MessageEntityType>(entity.Type), entity.Offset, entity.Length);
                        }
                    }

                    Log.Information("Total count of entities in message: {count}", newEntities.Count);

                    if (newEntities.Count == 0)
                    {
                        await client.SendMessage(chatId: sender.Id, text: "No links found, send /start");
                        Log.Warning("No entities found, returning");
                        return;
                    }

                    var mainMessage = messageWithEntities;

                    if (mainMessage.MediaGroupId != null)
                    {
                        var messageIDs = await client.CopyMessages(
                                chatId: sender.Id, 
                                fromChatId: sender.Id,
                                messageIds: messages.Select(m => m.MessageId).Order());

                        await client.EditMessageCaption(
                                chatId: sender.Id,
                                messageId: messageIDs.MinBy(mi => mi.Id),
                                caption: mainMessage.Caption,
                                captionEntities: newEntities,
                                replyMarkup: mainMessage.ReplyMarkup);

                        Log.Information("Sent album with edited caption");
                    }
                    else
                    {
                        var messageId = await client.CopyMessage(
                                chatId: sender.Id, 
                                fromChatId: sender.Id,
                                messageId: mainMessage.MessageId);

                        if (mainMessage.Caption is not null)
                        {
                            await client.EditMessageCaption(
                                    chatId: sender.Id,
                                    messageId: messageId,
                                    caption: mainMessage.Caption,
                                    captionEntities: newEntities,
                                    replyMarkup: mainMessage.ReplyMarkup);
                        }
                        else
                        {
                            await client.EditMessageText(
                                    chatId: sender.Id,
                                    messageId: messageId,
                                    text: mainMessage.Text,
                                    entities: newEntities,
                                    linkPreviewOptions: mainMessage.LinkPreviewOptions,
                                    replyMarkup: mainMessage.ReplyMarkup);
                        }


                        Log.Information("Sent single message with edited caption");
                    }

                    return;

                default:
                    await client.SendMessage(chatId: sender.Id, text: $"Unknown command {commandText}");
                    return;
            }
        }

        bool multimessage = false;
        if (_messages.ContainsKey(sender.Id))
        {
            if (message.MediaGroupId is null || _messages[sender.Id].Any(m => m.MediaGroupId != message.MediaGroupId))
            {
                Log.Warning("Received message with MediaGroupId {mediaGroupId}, but expected {existingMediaGroupId}", message.MediaGroupId ?? "null", _messages[sender.Id].First().MediaGroupId);
                await client.SendMessage(chatId: sender.Id, text: "Send new link in format {REPLACE_COMMAND} <link> or send {START_COMMAND} to reset");
                return;
            }

            _messages[sender.Id].Add(message);
            multimessage = true;
        }
        else
        {
            _messages.Add(sender.Id, [ message ]);

            if (message.MediaGroupId != null)
                multimessage = true;
        }

        if (multimessage)
        {
            if (_waitingForMediaGroupSec[sender.Id] > 0)
            {
                ++_waitingForMediaGroupSec[sender.Id];
                return;
            }

            _waitingForMediaGroupSec[sender.Id] = 3;

            Task.Run(async () =>
            {
                while (_waitingForMediaGroupSec[sender.Id] > 0)
                {
                    --_waitingForMediaGroupSec[sender.Id];
                    await Task.Delay(1000);
                }

                if (_waitingForMediaGroupSec[sender.Id] == -1)
                    return;

                _waitingForMediaGroupSec[sender.Id] = -1;

                await client.SendMessage(chatId: sender.Id, text: $"Now send new link in format {REPLACE_COMMAND} <link> or send {START_COMMAND} to reset");
            });
        }
        else
        {
            await client.SendMessage(chatId: sender.Id, text: $"Now send new link in format {REPLACE_COMMAND} <link> or send {START_COMMAND} to reset");
        }

        return;
    }

    private static Logger InitLogger(string projectDirPath)
    {
        var logsDirPath = Path.Combine(projectDirPath, "logs");
        Directory.CreateDirectory(logsDirPath);
        var logFilePath = Path.Combine(logsDirPath, $"{PROJECT_NAME}.log");

        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(restrictedToMinimumLevel: LogEventLevel.Information)
            .CreateLogger();

        return logger;
    }

    private static Task OnError(ITelegramBotClient client, Exception exception, CancellationToken ct)
    {
        Log.Error(exception.ToString());
        return Task.CompletedTask;
    }

    private static string GetProjectDirPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var appDataDirPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appDataDirPath, PROJECT_NAME);
        }

        var homeDirPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(homeDirPath, ".config", PROJECT_NAME);
    }

    private static bool TryLoadConfig(string projectDirPath, out Config config)
    {
        var configFilePath = Path.Combine(projectDirPath, "config.json");
        config = Config.Load(configFilePath);
        return config is not null;
    }
}
