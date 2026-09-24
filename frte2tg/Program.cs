using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace frte2tg
{
    internal class Program
    {
        public static SettingsFile settings;
        public static ITelegramBotClient bot;
        static CancellationTokenSource tgPollingCts;
        public static SemaphoreSlim tgSemaphore = new SemaphoreSlim(1, 1);
        public static MqttClientFactory mqttFactory;
        public static IMqttClient mqttClient;
        public static MqttClientOptions mqttOptions;
        public static string appLocation;
        public static AIQueueService aiQueue;
        public static FRQueueService frQueue;
        public static bool goAI = false;
        public static bool goFR = false;

        static async Task Main(string[] args)
        {
            appLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            settings = new SettingsFile();
            string fs;
            if (args.Length == 0)
                fs = "/etc/frte2tg/frte2tg.yml";
            else
                fs = args[0];
            try
            {
                settings = (new DeserializerBuilder()
                                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                                .Build())
                           .Deserialize<SettingsFile>(System.IO.File.ReadAllText(fs));
            }
            catch
            {
                ConsoleLog("app", "", "", "Bad settings file. Exit");
                return;
            }

            Log("app", "", "", "frte2tg v" + VersionInfo.Informational + " started");
            WebUi.Start(fs);
            StatsService.StartClipCacheCleaner();
            await Initialize();
            Thread.Sleep(Timeout.Infinite);
        }

        public static async Task Initialize()
        {
            L10n.Load(settings.options.locale);

            if (goAI) { aiQueue.Stop(); goAI = false; }
            if (goFR) { frQueue.Stop(); goFR = false; }

            // Re-initialization (settings saved in the web UI) must stop the previous Telegram polling and MQTT client,
            // otherwise two pollers fight over getUpdates (409 Conflict) and the old client keeps reconnecting.
            tgPollingCts?.Cancel();
            tgPollingCts = new CancellationTokenSource();

            if (mqttClient != null)
            {
                var oldClient = mqttClient;
                oldClient.ApplicationMessageReceivedAsync -= MqttClientApplicationMessageReceivedAsync;
                oldClient.ConnectedAsync -= MqttClientConnectedAsync;
                oldClient.DisconnectedAsync -= MqttClientDisconnectedAsync;
                try
                {
                    if (oldClient.IsConnected)
                        await oldClient.DisconnectAsync();
                }
                catch (Exception ex) { Log("app", "", "", "Error while disconnecting from mqtt server: " + ex.Message); }
                oldClient.Dispose();
                mqttClient = null;
            }

            bot = new TelegramBotClient(
                new TelegramBotClientOptions(
                    token: settings.telegram.token,
                    baseUrl: string.IsNullOrEmpty(settings.telegram.apiserver) ? null : settings.telegram.apiserver));
            bot.StartReceiving(
                updateHandler: TgHandleUpdateAsync,
                errorHandler: TgHandlePollingErrorAsync,
                receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                cancellationToken: tgPollingCts.Token);
            _ = Task.Run(() => bot.GetMe());
            _ = Task.Run(async () =>
            {
                try
                {
                    await bot.SetMyCommands(new[]
                    {
                        new BotCommand { Command = "status", Description = L10n.T("tg.cmd.status") },
                        new BotCommand { Command = "last", Description = L10n.T("tg.cmd.last") },
                        new BotCommand { Command = "stat", Description = L10n.T("tg.cmd.stat") },
                        new BotCommand { Command = "help", Description = L10n.T("tg.cmd.help") },
                    });
                }
                catch (Exception ex) { Log("app", "", "", "Failed to set bot commands: " + ex.Message); }
            });
            Log("app", "", "", "Telegram bot polling started");

            if (settings.ai != null && !string.IsNullOrEmpty(settings.ai.url) && !string.IsNullOrEmpty(settings.ai.model))
            {
                aiQueue = new AIQueueService(bot, settings.ai);
                aiQueue.Start();
                goAI = true;
            }
            else Log("app", "", "", "AI service not configured, skipping");

            if (settings.fr != null && !string.IsNullOrEmpty(settings.fr.url) && !string.IsNullOrEmpty(settings.fr.apikey))
            {
                frQueue = new FRQueueService(bot, settings.fr);
                frQueue.Start();
                goFR = true;
            }
            else Log("app", "", "", "FR service not configured, skipping");

            try
            {
                mqttFactory = new MqttClientFactory();
                mqttClient = mqttFactory.CreateMqttClient();
                mqttOptions = new MqttClientOptionsBuilder()
                    .WithClientId("frte2tg")
                    .WithTcpServer(settings.mqtt.host, settings.mqtt.port)
                    .WithCredentials(settings.mqtt.user, settings.mqtt.password)
                    .WithCleanSession()
                    .Build();
                mqttClient.ApplicationMessageReceivedAsync += MqttClientApplicationMessageReceivedAsync;
                mqttClient.ConnectedAsync += MqttClientConnectedAsync;
                mqttClient.DisconnectedAsync += MqttClientDisconnectedAsync;
                await mqttClient.ConnectAsync(mqttOptions);
                Log("app", "", "", "Waiting for mqtt messages");
                var mqttSubscribeOptions = new MqttClientSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(settings.mqtt.eventstopic))
                    .WithTopicFilter(f => f.WithTopic(settings.mqtt.reviewstopic))
                    .Build();
                await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
            }
            catch (Exception ex)
            {
                ConsoleLog("app", "", "", ex.ToString());
            }
        }

        private static async Task<T> TgCall<T>(Func<Task<T>> call, string type, string eventId, string camera)
        {
            await tgSemaphore.WaitAsync();
            try
            {
                while (true)
                {
                    try
                    {
                        var result = await call();
                        await Task.Delay(50);
                        return result;
                    }
                    catch (ApiRequestException ex) when (ex.ErrorCode == 429)
                    {
                        int wait = (ex.Parameters?.RetryAfter ?? settings.telegram.retryonratelimit) * 1000;
                        Log(type, eventId, camera, $"Telegram rate limit, waiting {wait / 1000}s");
                        await Task.Delay(wait);
                    }
                }
            }
            finally
            {
                tgSemaphore.Release();
            }
        }

        static string LiveSnapshotDir => appLocation + "/live";

        // Frigate writes {camera}-{id}.jpg to clips only when an event ends, but a review can end while one of its
        // detections is still going on (e.g. a parked car). For such an event the current best frame is taken from
        // the Frigate API and saved to LiveSnapshotDir. Returns null while nothing is available yet.
        static async Task<string> ResolveSnapshotAsync(string camera, string eventId, string type, string logId)
        {
            string clip = settings.frigate.clipspath + "/" + camera + "-" + eventId + ".jpg";
            if (System.IO.File.Exists(clip))
                return clip;

            // Ended but the file isn't written yet: keep waiting for Frigate's own snapshot.
            var ev = StatsService.GetEvent(eventId);
            if (ev != null && ev.end_time != null)
                return null;

            var bytes = await StatsService.GetFrigateSnapshotAsync(eventId);
            if (bytes == null)
                return null;

            Directory.CreateDirectory(LiveSnapshotDir);
            foreach (var old in Directory.GetFiles(LiveSnapshotDir).Where(f => System.IO.File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-1)))
                try { System.IO.File.Delete(old); } catch { }

            string path = LiveSnapshotDir + "/" + camera + "-" + eventId + ".jpg";
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            Log(type, logId, camera, "Event " + eventId + " is still in progress, using its current snapshot from Frigate");
            return path;
        }

        // Waits (up to options.timeout) until every event has a snapshot; returns event id -> snapshot path for those found.
        static async Task<Dictionary<string, string>> WaitSnapshotsAsync(string camera, IEnumerable<string> eventIds, string type, string logId)
        {
            var ids = eventIds.ToList();
            var found = new Dictionary<string, string>();
            int secs = 0;
            while (true)
            {
                foreach (var id in ids.Where(i => !found.ContainsKey(i)).ToList())
                {
                    string path = await ResolveSnapshotAsync(camera, id, type, logId);
                    if (path != null)
                        found[id] = path;
                }
                if (found.Count == ids.Count || secs >= settings.options.timeout)
                    return found;
                secs += settings.options.retry;
                await Task.Delay(settings.options.retry * 1000);
            }
        }

        private static List<(int partId, int totalParts, string path)> BuildFfmpegParts(string id, string camera, List<DbRow> dl)
        {
            var result = new List<(int, int, string)>();
            long totalSize = dl.Sum(x => x.size);

            var chunks = new List<List<DbRow>>();

            if (totalSize <= settings.telegram.clipsizecheck)
            {
                chunks.Add(dl);
            }
            else
            {
                Log("review", id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
                int start = 0;
                while (start < dl.Count)
                {
                    long currSize = 0;
                    int end = start;
                    for (int j = start; j < dl.Count; j++)
                    {
                        if (currSize + dl[j].size <= settings.telegram.clipsizesplit)
                        {
                            currSize += dl[j].size;
                            end = j;
                        }
                        else
                        {
                            if (j == start) end = j;
                            break;
                        }
                    }
                    chunks.Add(dl.GetRange(start, end - start + 1));
                    start = end + 1;
                }
            }

            int total = chunks.Count;
            for (int i = 0; i < chunks.Count; i++)
            {
                int partId = i + 1;
                string suffix = (total == 1) ? "" : "-part" + partId;
                string txtPath = Path.Combine(appLocation, id + suffix + ".txt");
                string mp4Path = Path.Combine(appLocation, id + suffix + ".mp4");

                System.IO.File.WriteAllLines(txtPath, chunks[i].Select(x => "file '" + x.realpath + "'"));
                RunFfmpeg(txtPath, mp4Path);
                result.Add((partId, total, mp4Path));
            }

            Log("review", id, camera, (total == 1) ? "File is prepared by ffmpeg for sending" : total + " files are prepared by ffmpeg for sending");

            return result;
        }

        private static void RunFfmpeg(string txtPath, string mp4Path)
        {
            string args = $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{txtPath}\" -c copy \"{mp4Path}\"";
            using var p = Process.Start("ffmpeg", args);
            p.WaitForExit();
            System.IO.File.Delete(txtPath);
        }
        private static string RunFfmpegGif(string mp4Path, int width = 320)
        {
            string gifPath = mp4Path.Replace(".mp4", ".gif");
            string args = $"-y -hide_banner -loglevel error -i \"{mp4Path}\" -r 8 -vf \"setpts=0.12*PTS,scale={width}:-1\" -loop 0 \"{gifPath}\"";
            using var p = Process.Start("ffmpeg", args);
            p.WaitForExit();
            return gifPath;
        }

        async static Task FrigateReviewNewWorker(FrigateReview fr)
        {
            try
            {
                string camera = fr.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("review", fr.after.id, camera, "Start review new/update worker");

                List<string> rulabels = new List<string>();
                if (fr.after.data?.objects != null)
                    foreach (var ob in fr.after.data.objects)
                        rulabels.Add(L10n.Label(ob.ToLower()));

                if (settings.frigate.cameras[cami].snapshot)
                {
                    var snaps = await WaitSnapshotsAsync(camera, fr.after.data.detections, "review", fr.after.id);
                    if (snaps.Count < fr.after.data.detections.Count)
                    {
                        Log("review", fr.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    string tgcaption = L10n.T("caption.review") + " \t" + fr.after.id + "\n" +
                                       L10n.T("caption.camera") + " " + fr.after.camera + "\n" +
                                       L10n.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                       L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fr.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       L10n.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

                    List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snaps[ev]), fr.after.camera + "-" + ev + ".jpg"))
                        {
                            Caption = (i == 1) ? tgcaption : null,
                            ParseMode = ParseMode.Markdown
                        });
                        if (i == 9) break;
                        i++;
                    }

                    if (md.Count > 0)
                    {
                        var imagePaths = fr.after.data.detections.Select(ev => snaps[ev]).ToList();
                        string aiPrompt = fr.after.data.objects.Contains("person") ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt;

                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md), "review", fr.after.id, camera);
                            await Task.Delay(100);
                            int firstmessageid = msgs[0].MessageId;
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid);

                            if (goFR && settings.frigate.cameras[cami].fr)
                                frQueue.AddToQueue(new FRTask
                                {
                                    ImagePaths = imagePaths,
                                    AIPrompt = aiPrompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessageid,
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                            else if (goAI && settings.frigate.cameras[cami].ai)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = imagePaths,
                                    Prompt = aiPrompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessageid,
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Error while review new/update worker: " + ex.Message);
            }
        }

        private static async Task SendReviewMediaAsync(
                                        FrigateReview fr,
                                        string camera,
                                        int cami,
                                        List<string> rulabels,
                                        List<IAlbumInputMedia> md,
                                        Dictionary<string, int> firstmessages,
                                        bool firstmessage,
                                        string tgcaption,
                                        List<DbRow> dl,
                                        Dictionary<string, string> snaps)
        {
            var parts = BuildFfmpegParts(fr.after.id, camera, dl);
            int partid = parts.Count;

            if (string.IsNullOrEmpty(tgcaption))
                tgcaption = L10n.T("caption.review") + " \t" + fr.after.id + "\n" +
                            L10n.T("caption.camera") + " " + fr.after.camera + "\n" +
                            L10n.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                            L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fr.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                            L10n.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

            // Snapshots resolved by the caller (includes current frames of events still in progress), otherwise Frigate's files.
            var imagePaths = fr.after.data.detections
                .Select(ev => snaps.TryGetValue(ev, out var p) ? p : settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg")
                .Where(p => System.IO.File.Exists(p))
                .ToList();
            string aiPrompt = fr.after.data.objects.Contains("person") ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt;

            if (settings.frigate.cameras[cami].gif)
            {
                string gifPath = RunFfmpegGif(parts[0].path, settings.options.gifwidth);
                if (System.IO.File.Exists(gifPath))
                {
                    try
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            await TgCall(() => bot.SendAnimation(
                                chatId: chid,
                                animation: InputFile.FromStream(System.IO.File.OpenRead(gifPath), Path.GetFileName(gifPath)),
                                caption: tgcaption,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "review", fr.after.id, camera);
                            await Task.Delay(100);
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The gif was sent to telegram chat " + chid);
                        }
                    }
                    finally
                    {
                        System.IO.File.Delete(gifPath);
                    }
                }
            }

            if (settings.frigate.cameras[cami].clip)
            {
                await Task.Delay(settings.options.retry * 100);

                if (settings.frigate.cameras[cami].sctogether && settings.frigate.cameras[cami].snapshot)
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        if (!firstmessage)
                        {
                            md.Add(new InputMediaVideo(
                                new InputFileStream(System.IO.File.OpenRead(parts[i - 1].path),
                                fr.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4")));

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                    Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md), "review", fr.after.id, camera);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("review", fr.after.id, camera, "The snapshot and clip were sent to telegram chat " + chid);
                                    x++;

                                    if (goFR && settings.frigate.cameras[cami].fr)
                                        frQueue.AddToQueue(new FRTask
                                        {
                                            ImagePaths = imagePaths,
                                            AIPrompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fr.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                    else if (goAI && settings.frigate.cameras[cami].ai)
                                        aiQueue.AddToQueue(new AITask
                                        {
                                            ImagePaths = imagePaths,
                                            Prompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fr.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                }
                            }
                        }
                        else
                        {
                            string cap = fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video");
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                await TgCall(() => bot.SendVideo(
                                    chatId: chid,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                    "review", fr.after.id, camera);
                                await Task.Delay(100);
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                x++;
                                Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                            }
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
                else
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            string cap = (firstmessages[chid] != -1)
                                ? fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video")
                                : fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video") + "\n" +
                                  L10n.T("caption.camera") + " " + fr.after.camera + "\n" +
                                  L10n.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                  L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fr.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                  L10n.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            await TgCall(() => bot.SendVideo(
                                chatId: chid,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "review", fr.after.id, camera);
                            await Task.Delay(100);
                            Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                            x++;
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
            }
            else
            {
                foreach (var part in parts)
                    System.IO.File.Delete(part.path);
            }
        }

        async static Task FrigateReviewEndWorker(FrigateReview fr)
        {
            try
            {
                string camera = fr.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("review", fr.after.id, camera, "Start review end worker");

                bool firstmessage = false;
                string tgcaption = "";
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();
                foreach (var chid in settings.telegram.chatids)
                    firstmessages.Add(chid, -1);

                List<string> rulabels = new List<string>();
                if (fr.after.data?.objects != null)
                    foreach (var ob in fr.after.data.objects)
                        rulabels.Add(L10n.Label(ob.ToLower()));

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                var snaps = new Dictionary<string, string>();

                if (settings.frigate.cameras[cami].snapshot && settings.frigate.cameras[cami].snapshottrigger == "end")
                {
                    snaps = await WaitSnapshotsAsync(camera, fr.after.data.detections, "review", fr.after.id);
                    if (snaps.Count < fr.after.data.detections.Count)
                        Log("review", fr.after.id, camera, "Some snapshots not ready after timeout, will skip missing");

                    tgcaption = L10n.T("caption.review") + " \t" + fr.after.id + "\n" +
                                L10n.T("caption.camera") + " " + fr.after.camera + "\n" +
                                L10n.T("caption.objects") + " " + string.Join(", ", rulabels) + "\n" +
                                L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                (fr.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                L10n.T("caption.events") + " " + string.Join(", ", fr.after.data.detections);

                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        if (!snaps.TryGetValue(ev, out string snapPath))
                        {
                            Log("review", fr.after.id, camera, "Snapshot not found, skipping: " + ev);
                            continue;
                        }
                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snapPath), fr.after.camera + "-" + ev + ".jpg"))
                        {
                            Caption = (i == 1) ? tgcaption : null,
                            ParseMode = ParseMode.Markdown
                        });
                        if (i == settings.telegram.mediagrouplimit - 1) break;
                        i++;
                    }

                    if ((md.Count > 0) && (!settings.frigate.cameras[cami].sctogether || !settings.frigate.cameras[cami].clip))
                    {
                        firstmessage = true;
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md), "review", fr.after.id, camera);
                            await Task.Delay(100);
                            firstmessages[chid] = msgs[0].MessageId;
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                        }

                        if (goFR && settings.frigate.cameras[cami].fr)
                            foreach (var chid in settings.telegram.chatids)
                                frQueue.AddToQueue(new FRTask
                                {
                                    ImagePaths = fr.after.data.detections.Where(snaps.ContainsKey).Select(ev => snaps[ev]).ToList(),
                                    AIPrompt = fr.after.data.objects.Contains("person") ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessages[chid],
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                        else if (goAI && settings.frigate.cameras[cami].ai)
                            foreach (var chid in settings.telegram.chatids)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = fr.after.data.detections.Where(snaps.ContainsKey).Select(ev => snaps[ev]).ToList(),
                                    Prompt = fr.after.data.objects.Contains("person") ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessages[chid],
                                    Camera = camera,
                                    EventId = fr.after.id,
                                    OriginalCaption = tgcaption
                                });
                    }
                }

                if (settings.frigate.cameras[cami].clip || settings.frigate.cameras[cami].gif)
                {
                    int secs = 0;
                    var sqlq = (sql: new Queries().getEventQuery("review", true), id: fr.after.id, camera: fr.after.camera);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                    bool isSuccess = false;

                    while (secs <= settings.options.timeout)
                    {
                        using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                        db.Open();
                        using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                        if (dr.HasRows)
                        {
                            isSuccess = true;
                            Log("review", fr.after.id, camera, "All recordings are ready");

                            if (settings.frigate.cameras[cami].trueend)
                            {
                                var fes = new FrigateReview { type = "trueend", before = fr.before, after = fr.after };
                                Log("review", fr.after.id, camera, "Sending the trueend review");
                                await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic(settings.mqtt.reviewstopic)
                                    .WithPayload(JsonConvert.SerializeObject(fes))
                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                    .WithRetainFlag()
                                    .Build(), CancellationToken.None);
                            }

                            List<DbRow> dl = new List<DbRow>();
                            while (dr.Read())
                                dl.Add(new DbRow
                                {
                                    path = (string)dr["path"],
                                    start_time = (double)dr["start_time"],
                                    end_time = (double)dr["end_time"],
                                    duration = (double)dr["duration"],
                                    realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                    size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                });
                            db.Close();
                            await Task.Delay(10);

                            await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl, snaps);
                            break;
                        }

                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!isSuccess)
                    {
                        if (settings.options.sendeverythingwhatyouhave)
                        {
                            Log("review", fr.after.id, camera, "Timeout expired, video files were not ready. Trying to send everything the frigate has");
                            using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = (new Queries().getEventQuery("review", false), fr.after.id, fr.after.camera);
                            db.Open();
                            using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("review", fr.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = new FrigateReview { type = "trueend", before = fr.before, after = fr.after };
                                    Log("review", fr.after.id, camera, "Sending the trueend review");
                                    await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                        .WithTopic(settings.mqtt.reviewstopic)
                                        .WithPayload(JsonConvert.SerializeObject(fes))
                                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                        .WithRetainFlag()
                                        .Build(), CancellationToken.None);
                                }

                                List<DbRow> dl = new List<DbRow>();
                                while (dr.Read())
                                    dl.Add(new DbRow
                                    {
                                        path = (string)dr["path"],
                                        start_time = (double)dr["start_time"],
                                        end_time = (double)dr["end_time"],
                                        duration = (double)dr["duration"],
                                        realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                        size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                    });
                                db.Close();
                                await Task.Delay(10);

                                await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl, snaps);
                            }
                        }
                        else
                            Log("review", fr.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Error in review end worker: " + ex.Message);
            }
        }


        async static Task FrigateEventNewWorker(FrigateEvent fe)
        {
            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("event", fe.after.id, camera, "Start event new/update worker");

                string rulabel = L10n.Label(fe.after.label.ToLower())
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                if (fe.after.has_snapshot && settings.frigate.cameras[cami].snapshot)
                {
                    var snaps = await WaitSnapshotsAsync(camera, new[] { fe.after.id }, "event", fe.after.id);
                    if (!snaps.TryGetValue(fe.after.id, out string snapshotPath))
                    {
                        Log("event", fe.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    string aiPrompt = fe.after.label == "person" ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt;
                    var imagePaths = new List<string> { snapshotPath };

                    int x = 1;
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = fe.after.id + " " + L10n.T("caption.photo") + "\n" +
                                           L10n.T("caption.camera") + " " + fe.after.camera + "\n" +
                                           L10n.T("caption.object") + " " + rulabel + "\n" +
                                           L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           (fe.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                           L10n.T("caption.event") + " " + fe.after.id;

                        Message msg = await TgCall(() => bot.SendPhoto(
                            chatId: chid,
                            photo: InputFile.FromStream(System.IO.File.OpenRead(snapshotPath)),
                            caption: tgcaption,
                            parseMode: ParseMode.Markdown),
                            "event", fe.after.id, camera);

                        await Task.Delay(100);
                        if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                        x++;

                        Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid);

                        if (goFR && settings.frigate.cameras[cami].fr)
                            frQueue.AddToQueue(new FRTask
                            {
                                ImagePaths = imagePaths,
                                AIPrompt = aiPrompt,
                                ChatId = long.Parse(chid),
                                MessageId = msg.MessageId,
                                Camera = camera,
                                EventId = fe.after.id,
                                OriginalCaption = tgcaption
                            });
                        else if (goAI && settings.frigate.cameras[cami].ai)
                            aiQueue.AddToQueue(new AITask
                            {
                                ImagePaths = imagePaths,
                                Prompt = aiPrompt,
                                ChatId = long.Parse(chid),
                                MessageId = msg.MessageId,
                                Camera = camera,
                                EventId = fe.after.id,
                                OriginalCaption = tgcaption
                            });
                    }
                }
            }
            catch (Exception ex)
            {
                Log("event", fe.after.id, fe.after.camera, "Error in event new/update worker: " + ex.Message);
            }
        }


        private static async Task SendEventMediaAsync(
                                        FrigateEvent fe,
                                        string camera,
                                        int cami,
                                        string rulabel,
                                        List<IAlbumInputMedia> md,
                                        Dictionary<string, int> firstmessages,
                                        bool firstmessage,
                                        string tgcaption,
                                        List<DbRow> dl)
        {
            var parts = BuildFfmpegParts(fe.after.id, camera, dl);
            int partid = parts.Count;

            if (string.IsNullOrEmpty(tgcaption))
                tgcaption = L10n.T("caption.event") + " \t" + fe.after.id + "\n" +
                            L10n.T("caption.camera") + " " + fe.after.camera + "\n" +
                            L10n.T("caption.object") + " " + rulabel + "\n" +
                            L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fe.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

            var imagePaths = new List<string> { settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg" };
            string aiPrompt = fe.after.label == "person" ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt;

            if (settings.frigate.cameras[cami].gif)
            {
                string gifPath = RunFfmpegGif(parts[0].path, settings.options.gifwidth);
                if (System.IO.File.Exists(gifPath))
                {
                    try
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            await TgCall(() => bot.SendAnimation(
                                chatId: chid,
                                animation: InputFile.FromStream(System.IO.File.OpenRead(gifPath), Path.GetFileName(gifPath)),
                                caption: tgcaption,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "event", fe.after.id, camera);
                            await Task.Delay(100);
                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            x++;
                            Log("event", fe.after.id, camera, "The gif was sent to telegram chat " + chid);
                        }
                    }
                    finally
                    {
                        System.IO.File.Delete(gifPath);
                    }
                }
            }

            if (settings.frigate.cameras[cami].clip)
            {
                await Task.Delay(settings.options.retry * 100);

                if (settings.frigate.cameras[cami].sctogether && settings.frigate.cameras[cami].snapshot)
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        if (!firstmessage)
                        {
                            md.Add(new InputMediaVideo(
                                new InputFileStream(System.IO.File.OpenRead(parts[i - 1].path),
                                fe.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4")));

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                    Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md), "event", fe.after.id, camera);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("event", fe.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid);
                                    x++;

                                    if (goFR && settings.frigate.cameras[cami].fr)
                                        frQueue.AddToQueue(new FRTask
                                        {
                                            ImagePaths = imagePaths,
                                            AIPrompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fe.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                    else if (goAI && settings.frigate.cameras[cami].ai)
                                        aiQueue.AddToQueue(new AITask
                                        {
                                            ImagePaths = imagePaths,
                                            Prompt = aiPrompt,
                                            ChatId = long.Parse(chid),
                                            MessageId = firstmessages[chid],
                                            Camera = camera,
                                            EventId = fe.after.id,
                                            OriginalCaption = tgcaption
                                        });
                                }
                            }
                        }
                        else
                        {
                            string cap = fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video");
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                await TgCall(() => bot.SendVideo(
                                    chatId: chid,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                    "event", fe.after.id, camera);
                                await Task.Delay(100);
                                Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                                x++;
                            }
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
                else
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {
                            string cap = (firstmessages[chid] != -1)
                                ? fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video")
                                : fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " " + L10n.T("caption.video") + "\n" +
                                  L10n.T("caption.camera") + " " + fe.after.camera + "\n" +
                                  L10n.T("caption.object") + " " + rulabel + "\n" +
                                  L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fe.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                            if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                            await TgCall(() => bot.SendVideo(
                                chatId: chid,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyParameters: (firstmessages[chid] != -1) ? new ReplyParameters { MessageId = firstmessages[chid] } : null),
                                "event", fe.after.id, camera);
                            await Task.Delay(100);
                            Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i + " ") + "was sent to telegram chat " + chid);
                            x++;
                        }

                        System.IO.File.Delete(parts[i - 1].path);
                    }
                }
            }
            else
            {
                foreach (var part in parts)
                    System.IO.File.Delete(part.path);
            }
        }


        async static Task FrigateEventEndWorker(FrigateEvent fe)
        {
            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("event", fe.after.id, camera, "Start event end worker");

                bool firstmessage = false;
                string tgcaption = "";
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();
                foreach (var chid in settings.telegram.chatids)
                    firstmessages.Add(chid, -1);

                string rulabel = L10n.Label(fe.after.label.ToLower())
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if (settings.frigate.cameras[cami].snapshot && settings.frigate.cameras[cami].snapshottrigger == "end")
                {
                    string snapPath = settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg";

                    int secs = 0;
                    while (secs <= settings.options.timeout)
                    {
                        if (System.IO.File.Exists(snapPath)) break;
                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!System.IO.File.Exists(snapPath))
                        Log("event", fe.after.id, camera, "Snapshot not ready after timeout, skipping");
                    else
                    {
                        tgcaption = L10n.T("caption.review") + " \t" + fe.after.id + "\n" +
                                    L10n.T("caption.camera") + " " + fe.after.camera + "\n" +
                                    L10n.T("caption.object") + " " + rulabel + "\n" +
                                    L10n.T("caption.start") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                    (fe.after.end_time.HasValue ? L10n.T("caption.end") + " " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(snapPath), fe.after.camera + "-" + fe.after.id + ".jpg"))
                        {
                            Caption = tgcaption,
                            ParseMode = ParseMode.Markdown
                        });

                        if ((md.Count > 0) && (!settings.frigate.cameras[cami].sctogether || !settings.frigate.cameras[cami].clip))
                        {
                            firstmessage = true;
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                Message[] msgs = await TgCall(() => bot.SendMediaGroup(chatId: chid, media: md), "event", fe.after.id, camera);
                                await Task.Delay(100);
                                firstmessages[chid] = msgs[0].MessageId;
                                if (x > 1) await Task.Delay(settings.telegram.sendchatstimepause * 1000);
                                x++;
                                Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                            }

                            if (goFR && settings.frigate.cameras[cami].fr)
                                foreach (var chid in settings.telegram.chatids)
                                    frQueue.AddToQueue(new FRTask
                                    {
                                        ImagePaths = new List<string> { snapPath },
                                        AIPrompt = fe.after.label == "person" ? settings.ai?.humanprompt : settings.ai?.nonhumanprompt,
                                        ChatId = long.Parse(chid),
                                        MessageId = firstmessages[chid],
                                        Camera = camera,
                                        EventId = fe.after.id,
                                        OriginalCaption = tgcaption
                                    });
                            else if (goAI && settings.frigate.cameras[cami].ai)
                                foreach (var chid in settings.telegram.chatids)
                                    aiQueue.AddToQueue(new AITask
                                    {
                                        ImagePaths = new List<string> { snapPath },
                                        Prompt = fe.after.label == "person" ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
                                        ChatId = long.Parse(chid),
                                        MessageId = firstmessages[chid],
                                        Camera = camera,
                                        EventId = fe.after.id,
                                        OriginalCaption = tgcaption
                                    });
                        }
                    }
                }

                if (fe.after.has_clip && (settings.frigate.cameras[cami].clip || settings.frigate.cameras[cami].gif))
                {
                    int secs = 0;
                    var sqlq = (sql: new Queries().getEventQuery("event", true), id: fe.after.id, camera: fe.after.camera);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                    bool isSuccess = false;

                    while (secs <= settings.options.timeout)
                    {
                        using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                        db.Open();
                        using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                        if (dr.HasRows)
                        {
                            isSuccess = true;
                            Log("event", fe.after.id, camera, "All recordings are ready");

                            if (settings.frigate.cameras[cami].trueend)
                            {
                                var fes = new FrigateEvent { type = "trueend", before = fe.before, after = fe.after };
                                Log("event", fe.after.id, camera, "Sending the trueend event");
                                await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                    .WithTopic(settings.mqtt.eventstopic)
                                    .WithPayload(JsonConvert.SerializeObject(fes))
                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                    .WithRetainFlag()
                                    .Build(), CancellationToken.None);
                            }

                            List<DbRow> dl = new List<DbRow>();
                            while (dr.Read())
                                dl.Add(new DbRow
                                {
                                    path = (string)dr["path"],
                                    start_time = (double)dr["start_time"],
                                    end_time = (double)dr["end_time"],
                                    duration = (double)dr["duration"],
                                    realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                    size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                });
                            db.Close();
                            await Task.Delay(10);

                            await SendEventMediaAsync(fe, camera, cami, rulabel, md, firstmessages, firstmessage, tgcaption, dl);
                            break;
                        }

                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!isSuccess)
                    {
                        if (settings.options.sendeverythingwhatyouhave)
                        {
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready. Trying to send everything Frigate has");
                            using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = (new Queries().getEventQuery("event", false), fe.after.id, fe.after.camera);
                            db.Open();
                            using var dr = RecordingsCommand(db, sqlq).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("event", fe.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = new FrigateEvent { type = "trueend", before = fe.before, after = fe.after };
                                    Log("event", fe.after.id, camera, "Sending the trueend event");
                                    await mqttClient.PublishAsync(new MqttApplicationMessageBuilder()
                                        .WithTopic(settings.mqtt.eventstopic)
                                        .WithPayload(JsonConvert.SerializeObject(fes))
                                        .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                        .WithRetainFlag()
                                        .Build(), CancellationToken.None);
                                }

                                List<DbRow> dl = new List<DbRow>();
                                while (dr.Read())
                                    dl.Add(new DbRow
                                    {
                                        path = (string)dr["path"],
                                        start_time = (double)dr["start_time"],
                                        end_time = (double)dr["end_time"],
                                        duration = (double)dr["duration"],
                                        realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                        size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                    });
                                db.Close();
                                await Task.Delay(10);

                                await SendEventMediaAsync(fe, camera, cami, rulabel, md, firstmessages, firstmessage, tgcaption, dl);
                            }
                        }
                        else
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch (Exception ex)
            {
                Log("event", fe.after.id, fe.after.camera, "Error in event end worker: " + ex.Message);
            }
        }


        async static Task MqttClientApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs arg)
        {
            try
            {
                var payloadText = Encoding.UTF8.GetString(arg.ApplicationMessage.Payload.ToArray());

                if (arg.ApplicationMessage.Topic == settings.mqtt.eventstopic)
                {
                    var fe = new FrigateEvent();
                    try
                    {
                        fe = JsonConvert.DeserializeObject<FrigateEvent>(payloadText, new JsonSerializerSettings
                        {
                            MissingMemberHandling = MissingMemberHandling.Ignore
                        });
                    }
                    catch
                    {
                        Log("app", "", "", "Bad payload");
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fe.after.camera);
                    if (cami == -1) return;

                    if (settings.frigate.cameras[cami].topic != "events")
                        return;

                    if ((fe != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fe.after.camera)) && (fe.type == "end"))
                    {

                        if ((!settings.frigate.cameras[cami].snapshot) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].snapshottrigger != fe.type) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fe.after.entered_zones).Count() == 0))
                            return;

                        if (EventPasses(settings.frigate.cameras[cami], fe.after))
                        {
                            Log("event", fe.after.id, fe.after.camera, "Event end received");
                            _ = Task.Run(() => FrigateEventEndWorker(fe: fe));
                        }
                    }

                    if ((fe != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fe.after.camera)) && ((fe.type == "new") || (fe.type == "update")))
                    {

                        if (!settings.frigate.cameras[cami].snapshot)
                            return;
                        if (settings.frigate.cameras[cami].snapshottrigger != fe.type)
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fe.after.entered_zones).Count() == 0))
                            return;

                        if (EventPasses(settings.frigate.cameras[cami], fe.after))
                        {
                            Log("event", fe.after.id, fe.after.camera, "Event new received");
                            _ = Task.Run(() => FrigateEventNewWorker(fe: fe));
                        }
                    }
                }

                if (arg.ApplicationMessage.Topic == settings.mqtt.reviewstopic)
                {
                    var fr = new FrigateReview();
                    try
                    {
                        fr = JsonConvert.DeserializeObject<FrigateReview>(payloadText, new JsonSerializerSettings
                        {
                            MissingMemberHandling = MissingMemberHandling.Ignore
                        });
                    }
                    catch
                    {
                        Log("app", "", "", "Bad payload");
                        return;
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fr.after.camera);
                    if (cami == -1) return;

                    if (settings.frigate.cameras[cami].topic != "reviews")
                        return;

                    if ((fr != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fr.after.camera)) && (fr.type == "end"))
                    {
                        if ((!settings.frigate.cameras[cami].snapshot) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if ((settings.frigate.cameras[cami].snapshottrigger != fr.type) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                            return;
                        if (!settings.frigate.cameras[cami].severity.Contains(fr.after.severity))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fr.after.data.zones).Count() == 0))
                            return;


                        if (ReviewPasses(settings.frigate.cameras[cami], fr))
                        {
                            Log("review", fr.after.id, fr.after.camera, "Review end received");
                            _ = Task.Run(() => FrigateReviewEndWorker(fr: fr));
                        }
                    }

                    if ((fr != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fr.after.camera)) && ((fr.type == "new") || (fr.type == "update")))
                    {

                        if (!settings.frigate.cameras[cami].snapshot)
                            return;
                        if (settings.frigate.cameras[cami].snapshottrigger != fr.type)
                            return;
                        if (!settings.frigate.cameras[cami].severity.Contains(fr.after.severity))
                            return;
                        if ((settings.frigate.cameras[cami].zones.Count > 0) && (settings.frigate.cameras[cami].zones.Intersect(fr.after.data.zones).Count() == 0))
                            return;

                        if (ReviewPasses(settings.frigate.cameras[cami], fr))
                        {
                            Log("review", fr.after.id, fr.after.camera, "Review new/update received");
                            _ = Task.Run(() => FrigateReviewNewWorker(fr: fr));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("mqtt", "", "", "Handler error: " + ex.Message);
            }

            return;
        }

        // `objects` filter of a camera: no list = everything passes; otherwise the label must be listed and
        // the score (0..1 from Frigate) must reach its `percent`.
        public static bool ObjectPasses(Camera cam, string label, double score)
        {
            if (cam.objects == null || cam.objects.Count == 0)
                return true;
            var obj = cam.objects.FirstOrDefault(o => o.label == label);
            return obj != null && score * 100 >= obj.percent;
        }

        // Events are judged by their best score so far (top_score), falling back to the current one.
        static bool EventPasses(Camera cam, BeforeAfterFE ev)
        {
            double score = Math.Max(ev.top_score, ev.score);
            if (ObjectPasses(cam, ev.label, score))
                return true;
            var obj = cam.objects.FirstOrDefault(o => o.label == ev.label);
            if (obj != null)
                Log("event", ev.id, ev.camera, $"Skipped: {ev.label} {Math.Round(score * 100)}% < {obj.percent}%");
            return false;
        }

        // Review messages carry no scores, so the thresholds are checked against the top_score of the review's
        // detections in Frigate's DB. Detections not in the DB yet (an early "new") are judged by label only.
        static bool ReviewPasses(Camera cam, FrigateReview fr)
        {
            if (cam.objects == null || cam.objects.Count == 0)
                return true;
            var labels = cam.objects.Select(o => o.label).ToList();
            if (fr.after.data?.objects == null || !fr.after.data.objects.Intersect(labels).Any())
                return false;

            List<EventRow> events;
            try
            {
                events = (fr.after.data.detections ?? new List<string>()).Select(StatsService.GetEvent).Where(e => e != null).ToList();
            }
            catch (Exception ex)
            {
                Log("review", fr.after.id, fr.after.camera, "Cannot check object thresholds, passing by label: " + ex.Message);
                return true;
            }
            if (events.Count == 0 || events.Any(e => ObjectPasses(cam, e.label, e.score)))
                return true;

            Log("review", fr.after.id, fr.after.camera, "Skipped: " + string.Join(", ", events
                .Where(e => labels.Contains(e.label))
                .Select(e => $"{e.label} {Math.Round(e.score * 100)}% < {cam.objects.First(o => o.label == e.label).percent}%")));
            return false;
        }

        async static Task MqttClientConnectedAsync(MqttClientConnectedEventArgs arg)
        {
            Log("app", "", "", "Connected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                                        .WithTopicFilter(x =>
                                            {
                                                x.WithTopic(settings.mqtt.eventstopic);
                                            })
                                        .WithTopicFilter(x =>
                                            {
                                                x.WithTopic(settings.mqtt.reviewstopic);
                                            })
                                        .Build();
            await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
            Log("app", "", "", "Subscribed to topics " + settings.mqtt.eventstopic + ", " + settings.mqtt.reviewstopic);
            return;
        }

        async static Task MqttClientDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
        {
            Log("app", "", "", "Disconnected from mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            await Task.Delay(TimeSpan.FromSeconds(5));

            try
            {
                await mqttClient.ConnectAsync(mqttOptions);
            }
            catch
            {
                Log("app", "", "", "Reconnecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed");
            }

            return;
        }

        async static Task DownloadFileAsync(string url, string filename)
        {
            try
            {
                using var http = new HttpClient();
                var data = await http.GetByteArrayAsync(url);
                await System.IO.File.WriteAllBytesAsync(filename, data);
            }
            catch (Exception)
            {
                Log("app", "", "", "Failed to download File: " + url);
            }
        }

        async static Task TgHandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
        {
            if (update.CallbackQuery is { } callback)
            {
                await TgHandleCallbackAsync(botClient, callback, cancellationToken);
                return;
            }

            if (update.Message is not { } message)
                return;
            if (message.Text is not { } messageText)
                return;

            Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Received message: " + messageText);

            if (!settings.telegram.chatids.Contains(message.Chat.Id.ToString()))
            {
                Log("tg", "", message.From.Id.ToString(), "Unauthorized access attempt from " + message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"));
                await TgCall(() => botClient.SendMessage(chatId: message.Chat.Id, text: L10n.T("tg.unauthorized"), cancellationToken: cancellationToken), "tg", "", message.Chat.Id.ToString());
                return;
            }

            string[] tokens = messageText.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                return;
            string command = tokens[0].ToLower();
            if (command.Contains('@'))
                command = command.Substring(0, command.IndexOf('@'));
            string[] args = tokens.Skip(1).ToArray();
            string who = message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")");

            if (command == "/last")
            {
                Log("tg", who, message.Chat.Id.ToString(), "Sending last");
                await TgSendLast(botClient, message.Chat.Id, args, cancellationToken);
            }

            if (command == "/stat")
            {
                Log("tg", who, message.Chat.Id.ToString(), "Sending stat");
                await TgSendStat(botClient, message.Chat.Id, null, args, cancellationToken);
            }

            if (command == "/private" || command == "/help" || command == "/start")
            {
                Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Sending help");
                string helpText = L10n.T("tg.help") + "\n\n" + L10n.T("tg.help.version", VersionInfo.Version);
                await TgCall(() => botClient.SendMessage(
                    chatId: message.Chat.Id,
                    text: helpText,
                    parseMode: ParseMode.Markdown,
                    cancellationToken: cancellationToken),
                    "tg", "", message.Chat.Id.ToString());
            }

            if (command == "/status")
            {
                Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Sending status");
                int? waitId = await TgSendWaitAsync(botClient, message.Chat.Id, L10n.T("tg.wait.status"), cancellationToken);
                using var db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                db.Open();
                using var dr = (new SqliteCommand(new Queries().getCamerasQuery(), db)).ExecuteReader();
                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                if (dr.HasRows)
                {
                    int i = 1;
                    while (dr.Read())
                    {
                        string rnd = RandomString(10);
                        string localPath = appLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg";
                        await DownloadFileAsync("http://" + settings.frigate.host + ":" + settings.frigate.port.ToString() + "/api/" + dr["camera"].ToString() + "/latest.jpg", localPath);

                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(localPath), dr["camera"].ToString() + "_" + rnd + ".jpg"))
                        {
                            Caption = ((i == 1) || (i % 11 == 0)) ? L10n.T("tg.status.caption") : null
                        });

                        if (i % 10 == 0)
                        {
                            await TgCall(() => botClient.SendMediaGroup(chatId: message.Chat.Id, media: md.ToList()), "tg", "", message.Chat.Id.ToString());
                            md.Clear();
                            Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Status batch sent");
                        }

                        System.IO.File.Delete(localPath);
                        i++;
                    }
                }
                dr.Close();
                db.Close();

                if (md.Count > 0)
                {
                    await TgCall(() => botClient.SendMediaGroup(chatId: message.Chat.Id, media: md.ToList()), "tg", "", message.Chat.Id.ToString());
                    Log("tg", message.From.Id + (string.IsNullOrEmpty(message.From.Username) ? "" : " (@" + message.From.Username + ")"), message.Chat.Id.ToString(), "Status sent");
                }
                await TgDeleteWaitAsync(botClient, message.Chat.Id, waitId);
            }
        }

        public static Dictionary<string, string> emojiobj = new Dictionary<string, string>()
                                                    {
                                                        { "car", "🚗" },
                                                        { "person", "👤" },
                                                        { "dog", "🐕" },
                                                        { "cat", "🐈" },
                                                        { "bird", "🐦" }
                                                    };

        static string EmojiLabel(string label) => emojiobj.TryGetValue(label, out var em) ? em : "•";

        static async Task TgHandleCallbackAsync(ITelegramBotClient botClient, CallbackQuery callback, CancellationToken cancellationToken)
        {
            if (callback.Message == null || callback.Data == null)
                return;
            long chatId = callback.Message.Chat.Id;
            string who = callback.From.Id + (string.IsNullOrEmpty(callback.From.Username) ? "" : " (@" + callback.From.Username + ")");

            if (!settings.telegram.chatids.Contains(chatId.ToString()))
            {
                Log("tg", who, chatId.ToString(), "Unauthorized callback from " + who);
                return;
            }

            Log("tg", who, chatId.ToString(), "Received callback: " + callback.Data);
            try { await botClient.AnswerCallbackQuery(callback.Id, cancellationToken: cancellationToken); }
            catch (ApiRequestException) { }

            // last|<camera or label>   stat|<period>|<camera>|<label>
            string[] parts = callback.Data.Split('|');
            if (parts[0] == "last" && parts.Length == 2)
                await TgSendLast(botClient, chatId, new[] { parts[1] }, cancellationToken);
            else if (parts[0] == "stat" && parts.Length == 4)
                await TgSendStat(botClient, chatId, callback.Message.Id,
                                 new[] { parts[1], parts[2], parts[3] }.Where(s => s.Length > 0).ToArray(), cancellationToken);
        }

        static async Task TgSendLast(ITelegramBotClient botClient, long chatId, string[] args, CancellationToken cancellationToken)
        {
            int? waitId = null;
            try
            {
                CommandFilter f = StatsService.ParseArgs(args);
                if (f.unknown.Count > 0)
                {
                    await TgSendUnknown(botClient, chatId, f.unknown, cancellationToken);
                    return;
                }

                waitId = await TgSendWaitAsync(botClient, chatId, L10n.T("tg.wait.last"), cancellationToken);

                // No camera given: last N events of every camera (N defaults to 1). Camera given: last N of that camera (N defaults to 5).
                bool overview = f.camera == null && f.label == null && f.limit == null;
                bool perCamera = f.camera == null;
                int limit = Math.Clamp(f.limit ?? (perCamera ? 1 : 5), 1, settings.telegram.mediagrouplimit);
                List<EventRow> rows = perCamera ? StatsService.GetLastPerCamera(limit, f.label) : StatsService.GetLast(f.camera, f.label, limit);

                if (rows.Count == 0)
                    await TgCall(() => botClient.SendMessage(chatId, L10n.T("tg.last.none"), cancellationToken: cancellationToken), "tg", "", chatId.ToString());

                var snaps = new List<(EventRow row, byte[] jpg)>();
                foreach (var r in rows)
                    snaps.Add((r, await StatsService.GetSnapshotAsync(r)));
                var withoutSnap = snaps.Where(s => s.jpg == null).Select(s => s.row).ToList();

                // One album per camera when several events per camera were asked for, otherwise one shared album.
                string filterTitle = f.label != null ? " · " + EmojiLabel(f.label) + " " + WebUtility.HtmlEncode(L10n.Label(f.label)) : "";
                var groups = perCamera && limit > 1
                    ? snaps.Where(s => s.jpg != null).GroupBy(s => s.row.camera)
                           .Select(g => (title: "<b>📷 " + WebUtility.HtmlEncode(g.Key) + "</b>" + filterTitle, items: g.ToList()))
                    : new[] { (title: (perCamera ? "<b>" + L10n.T("tg.last.title_all") + "</b>" : "<b>📷 " + WebUtility.HtmlEncode(f.camera) + "</b>") + filterTitle,
                                items: snaps.Where(s => s.jpg != null).ToList()) };

                foreach (var (title, items) in groups)
                foreach (var chunkSnaps in items.Chunk(settings.telegram.mediagrouplimit))
                {
                    var chunk = chunkSnaps.Select(s => s.row).ToArray();
                    // Telegram shows an album caption only when a single item has one, so all events are listed on the first photo.
                    string caption = title + "\n" +
                                     string.Join("\n", chunk.Select((r, i) => (i + 1) + ". " + FormatEventLine(r)));
                    var streams = chunkSnaps.Select(s => (Stream)new MemoryStream(s.jpg)).ToList();
                    try
                    {
                        if (chunk.Length == 1)
                            await TgCall(() => botClient.SendPhoto(chatId, new InputFileStream(streams[0], chunk[0].camera + "-" + chunk[0].id + ".jpg"),
                                                                    caption: caption, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                        else
                        {
                            var md = chunk.Select((r, i) => (IAlbumInputMedia)new InputMediaPhoto(new InputFileStream(streams[i], r.camera + "-" + r.id + ".jpg"))
                            {
                                Caption = i == 0 ? caption : null,
                                ParseMode = ParseMode.Html
                            }).ToList();
                            await TgCall(() => botClient.SendMediaGroup(chatId, md, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                        }
                    }
                    finally
                    {
                        streams.ForEach(s => s.Dispose());
                    }
                }

                if (withoutSnap.Count > 0)
                {
                    string text = "<b>" + L10n.T("tg.last.no_snapshot") + "</b>\n" + string.Join("\n", withoutSnap.Select(FormatEventLine));
                    await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                }

                if (overview)
                {
                    MetaResult meta = StatsService.GetMeta();
                    var buttons = new[] { 3, 5 }.Select(n => (text: L10n.T("tg.last.per_camera", n), data: CallbackData("last|" + n)))
                                  .Concat(meta.cameras.Select(c => (text: "📷 " + c, data: CallbackData("last|" + c))))
                                  .Concat(meta.labels.Select(l => (text: EmojiLabel(l) + " " + L10n.Label(l), data: CallbackData("last|" + l))))
                                  .Where(b => b.data != null)
                                  .Select(b => InlineKeyboardButton.WithCallbackData(b.text, b.data))
                                  .Chunk(3);
                    await TgCall(() => botClient.SendMessage(chatId, L10n.T("tg.last.pick"),
                                                             replyMarkup: new InlineKeyboardMarkup(buttons), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                }
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Error: /last failed: " + ex.Message);
                await TgCall(() => botClient.SendMessage(chatId, L10n.T("tg.error", ex.Message), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
            }
            finally
            {
                await TgDeleteWaitAsync(botClient, chatId, waitId);
            }
        }

        // Sends the stats message, or edits `editMessageId` in place when a period button was pressed.
        static async Task TgSendStat(ITelegramBotClient botClient, long chatId, int? editMessageId, string[] args, CancellationToken cancellationToken)
        {
            try
            {
                CommandFilter f = StatsService.ParseArgs(args);
                if (f.unknown.Count > 0)
                {
                    await TgSendUnknown(botClient, chatId, f.unknown, cancellationToken);
                    return;
                }

                string suffix = "|" + (f.camera ?? "") + "|" + (f.label ?? "");
                var periods = new[] { "24h", "today", "7d", "30d" }.Select(p => (L10n.T("tg.stat.btn." + p), p));
                var keyboard = new InlineKeyboardMarkup(periods
                    .Select(p => InlineKeyboardButton.WithCallbackData(p.Item1, CallbackData("stat|" + p.Item2 + suffix) ?? "stat|" + p.Item2 + "||")));

                // Visible reaction right away: a period button turns the message into "calculating…", a command gets a wait message.
                StatsService.TryParsePeriod(f.period, out _, out string periodTitle);
                string waitText = L10n.T("tg.wait.stat", periodTitle);
                int? waitId = null;
                if (editMessageId.HasValue)
                {
                    try
                    {
                        await TgCall(() => botClient.EditMessageText(chatId, editMessageId.Value, "⏳ " + waitText, replyMarkup: keyboard,
                                                                     cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                    }
                    catch (ApiRequestException ex) when (ex.Message.Contains("not modified")) { }
                }
                else
                    waitId = await TgSendWaitAsync(botClient, chatId, waitText, cancellationToken);

                StatsResult st = StatsService.GetStats(f.period, f.camera, f.label);
                string text = FormatStat(st) + "\n<i>" + L10n.T("tg.stat.updated", DateTime.UtcNow.AddMinutes(settings.options.timeoffset).ToString("HH:mm:ss")) + "</i>";

                if (editMessageId.HasValue)
                    await TgCall(() => botClient.EditMessageText(chatId, editMessageId.Value, text, parseMode: ParseMode.Html,
                                                                 replyMarkup: keyboard, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                else
                {
                    await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html,
                                                             replyMarkup: keyboard, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                    await TgDeleteWaitAsync(botClient, chatId, waitId);
                }
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Error: /stat failed: " + ex.Message);
                await TgCall(() => botClient.SendMessage(chatId, L10n.T("tg.error", ex.Message), cancellationToken: cancellationToken), "tg", "", chatId.ToString());
            }
        }

        // Slow commands first post a "please wait" message (as y2tav does) and delete it once the result is sent.
        static async Task<int?> TgSendWaitAsync(ITelegramBotClient botClient, long chatId, string text, CancellationToken cancellationToken)
        {
            try
            {
                var msg = await TgCall(() => botClient.SendMessage(chatId, "⏳ " + text, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
                return msg.MessageId;
            }
            catch (Exception ex)
            {
                Log("tg", "", chatId.ToString(), "Failed to send the wait message: " + ex.Message);
                return null;
            }
        }

        static async Task TgDeleteWaitAsync(ITelegramBotClient botClient, long chatId, int? messageId)
        {
            if (messageId == null)
                return;
            try { await botClient.DeleteMessage(chatId, messageId.Value); }
            catch (Exception ex) { Log("tg", "", chatId.ToString(), "Failed to delete the wait message: " + ex.Message); }
        }

        static async Task TgSendUnknown(ITelegramBotClient botClient, long chatId, List<string> unknown, CancellationToken cancellationToken)
        {
            MetaResult meta = StatsService.GetMeta();
            string text = L10n.T("tg.unknown", WebUtility.HtmlEncode(string.Join(", ", unknown))) + "\n\n" +
                          "<b>" + L10n.T("tg.unknown.cameras") + "</b> " + WebUtility.HtmlEncode(string.Join(", ", meta.cameras)) + "\n" +
                          "<b>" + L10n.T("tg.unknown.objects") + "</b> " + WebUtility.HtmlEncode(string.Join(", ", meta.labels)) + "\n" +
                          "<b>" + L10n.T("tg.unknown.periods") + "</b> 24h, 7d, 30d, today";
            await TgCall(() => botClient.SendMessage(chatId, text, parseMode: ParseMode.Html, cancellationToken: cancellationToken), "tg", "", chatId.ToString());
        }

        // Telegram limits callback data to 64 bytes.
        static string CallbackData(string data) => Encoding.UTF8.GetByteCount(data) <= 64 ? data : null;

        // Time only for today, date + time otherwise.
        static string FormatShortTime(double unix, string todayFormat)
        {
            DateTime t = StatsService.ToLocal(unix);
            return t.Date == DateTime.UtcNow.AddMinutes(settings.options.timeoffset).Date ? t.ToString(todayFormat) : t.ToString("dd.MM HH:mm");
        }

        static string FormatEventLine(EventRow r)
        {
            string time = FormatShortTime(r.start_time, "HH:mm:ss");
            return WebUtility.HtmlEncode(r.camera) + " · " + EmojiLabel(r.label) + " " + WebUtility.HtmlEncode(L10n.Label(r.label)) +
                   (r.sub_label != null ? " (" + WebUtility.HtmlEncode(r.sub_label) + ")" : "") +
                   " · " + Math.Round(r.score * 100) + "% · " + time + (r.end_time == null ? " · ⏳ " + L10n.T("tg.last.in_progress") : "") +
                   (r.zones.Count > 0 ? " · " + WebUtility.HtmlEncode(string.Join(", ", r.zones)) : "");
        }

        static string Sparkline(IEnumerable<int> values)
        {
            const string bars = "▁▂▃▄▅▆▇█";
            var list = values.ToList();
            int max = list.DefaultIfEmpty(0).Max();
            return new string(list.Select(v => v == 0 ? ' ' : bars[Math.Min(bars.Length - 1, (int)Math.Ceiling((double)v / max * bars.Length) - 1)]).ToArray());
        }

        static string FormatStat(StatsResult st)
        {
            var sb = new StringBuilder();
            sb.Append("<b>").Append(L10n.T("tg.stat.title", st.period)).Append("</b>");
            if (st.camera != null) sb.Append(" · 📷 ").Append(WebUtility.HtmlEncode(st.camera));
            if (st.label != null) sb.Append(" · ").Append(EmojiLabel(st.label)).Append(' ').Append(WebUtility.HtmlEncode(L10n.Label(st.label)));
            sb.Append('\n');
            sb.Append(L10n.T("tg.stat.summary", st.total, st.alerts, st.detections)).Append('\n');

            if (st.total == 0)
                return sb.Append("\n" + L10n.T("tg.stat.empty")).ToString();

            sb.Append("\n<b>" + L10n.T("tg.stat.by_object") + "</b>\n<pre>");
            int lw = st.labels.Max(l => L10n.Label(l).Length);
            foreach (var l in st.labels)
                sb.Append(EmojiLabel(l)).Append(' ').Append(WebUtility.HtmlEncode(L10n.Label(l).PadRight(lw))).Append(' ').Append(st.labelTotals[l].ToString().PadLeft(5)).Append('\n');
            sb.Append("</pre>");

            if (st.camera == null)
            {
                // A grid: the cells hold only numbers, emoji go to the header row alone, since their width varies
                // between clients. Up to 4 object columns; with more, the 3 most frequent plus "•" for the rest,
                // so the table still fits a phone screen.
                var labels = st.labels.Count <= 4 ? st.labels : st.labels.Take(3).ToList();
                var columns = new List<(string head, int headWidth, Func<string, int> value)>
                {
                    ("Σ", 1, c => st.matrix[c].Values.Sum())
                };
                foreach (var l in labels)
                    columns.Add((EmojiLabel(l), emojiobj.ContainsKey(l) ? 2 : 1, c => st.matrix[c].GetValueOrDefault(l)));
                if (st.labels.Count > 4)
                    columns.Add(("•", 1, c => st.matrix[c].Where(kv => !labels.Contains(kv.Key)).Sum(kv => kv.Value)));

                static string Cell(int v) => v == 0 ? "·" : v.ToString();
                int nameWidth = st.cameras.Max(c => c.Length);
                var widths = columns.Select(col => Math.Max(4, st.cameras.Max(c => Cell(col.value(c)).Length))).ToList();

                sb.Append("\n<b>" + L10n.T("tg.stat.by_camera") + "</b>\n<pre>");
                sb.Append(new string(' ', nameWidth));
                for (int i = 0; i < columns.Count; i++)
                    sb.Append(' ').Append(new string(' ', widths[i] - columns[i].headWidth)).Append(columns[i].head);
                sb.Append("    ⏱\n");
                foreach (var c in st.cameras)
                {
                    sb.Append(WebUtility.HtmlEncode(c.PadRight(nameWidth)));
                    for (int i = 0; i < columns.Count; i++)
                        sb.Append(' ').Append(Cell(columns[i].value(c)).PadLeft(widths[i]));
                    // Always 5 characters: time for today, date for earlier days.
                    if (st.lastByCamera.TryGetValue(c, out double last))
                    {
                        DateTime t = StatsService.ToLocal(last);
                        bool today = t.Date == DateTime.UtcNow.AddMinutes(settings.options.timeoffset).Date;
                        sb.Append(' ').Append(t.ToString(today ? "HH:mm" : "dd.MM"));
                    }
                    sb.Append('\n');
                }
                sb.Append("</pre>");
            }
            else if (st.lastByCamera.TryGetValue(st.camera, out double last))
                sb.Append(L10n.T("tg.stat.last_event", FormatShortTime(last, "HH:mm:ss"))).Append('\n');

            sb.Append("\n<b>" + L10n.T("tg.stat.by_hour") + "</b> · " + L10n.T("tg.stat.peak") + " ").Append(st.peakHour.ToString("00")).Append(":00–").Append(((st.peakHour + 1) % 24).ToString("00")).Append(":00\n");
            sb.Append("<pre>").Append(Sparkline(st.hours)).Append("\n0     6     12    18   23</pre>");

            if (st.days.Count > 2)
            {
                sb.Append("\n<b>" + L10n.T("tg.stat.by_day") + "</b> · ").Append(DateTime.Parse(st.days[0].day).ToString("dd.MM")).Append(" – ")
                  .Append(DateTime.Parse(st.days[^1].day).ToString("dd.MM")).Append('\n');
                sb.Append("<pre>").Append(Sparkline(st.days.Select(d => d.count))).Append("</pre>");
            }
            return sb.ToString();
        }

        static Task TgHandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
            {
                var ErrorMessage = exception switch
                {
                    ApiRequestException
                    apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                    _ => exception.ToString()
                };

                Log("app", "", "", ErrorMessage);
                return Task.CompletedTask;
            }

        private static Random random = new Random();
        public static string RandomString(int length)
        {
            return new string(Enumerable.Repeat("abcdefghijklmnopqrstuvwxyz0123456789", length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static void Log(string type, string eventid, string camera, string txt)
        {
            if (settings.logger.console)
                ConsoleLog(type, eventid, camera, txt);
            if (settings.logger.file)
                FileLog(type, eventid, camera, txt);
        }

        // Recordings of an event/review (Queries.getEventQuery) with its id and camera passed as parameters.
        static SqliteCommand RecordingsCommand(SqliteConnection db, (string sql, string id, string camera) q)
        {
            var cmd = new SqliteCommand(q.sql, db);
            cmd.Parameters.AddWithValue("$id", q.id);
            cmd.Parameters.AddWithValue("$camera", q.camera);
            return cmd;
        }

        static readonly object logLock = new object();

        // Local time for log lines: UTC + options.timeoffset (the container runs in UTC).
        static DateTime LogNow => DateTime.UtcNow.AddMinutes(settings.options.timeoffset);

        // Events are handled in parallel, so writes are serialized; a failed write must not break the caller.
        public static void FileLog(string type, string eventid, string camera, string txt)
        {
            try
            {
                DateTime now = LogNow;
                lock (logLock)
                {
                    Directory.CreateDirectory("/var/log/frte2tg/");
                    System.IO.File.AppendAllText("/var/log/frte2tg/frte2tg_" + now.ToString("yyyy-MM-dd") + ".log",
                                                 now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt + "\n");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to write the log file: " + ex.Message);
            }
        }

        public static void ConsoleLog(string type, string eventid, string camera, string txt)
        {
            Console.WriteLine(LogNow.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt);
        }

    }

}
