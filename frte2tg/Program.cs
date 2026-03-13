using Microsoft.Data.Sqlite;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace frte2tg
{
    internal class Program
    {
        public static SettingsFile settings;
        public static Dictionary<string, string> rumapobj = new Dictionary<string, string>()
                                                    {
                                                        { "car", "машина" },
                                                        { "person", "человек" },
                                                        { "dog", "собака" },
                                                        { "cat", "кошка" },
                                                        { "bird", "птица" }
                                                    };
        public static ITelegramBotClient bot;
        public static MqttFactory mqttFactory;
        public static IMqttClient mqttClient;
        public static MqttClientOptions mqttOptions;
        public static string appLocation;
        public static AIQueueService aiQueue;
        public static bool goAI = false;

        static async Task Main(string[] args)
        {

            appLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            settings = new SettingsFile();
            string fs;
            if (args.Length == 0)
                fs = "/etc/frte2tg"
                   //appLocation 
                   + "/frte2tg.yml";
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
                ConsoleLog("application", "", "", "Bad settings file. Exit");
                return;
            }

            bot = new TelegramBotClient(
                            new TelegramBotClientOptions(
                            token: settings.telegram.token,
                            baseUrl: string.IsNullOrEmpty(settings.telegram.apiserver) ? null : settings.telegram.apiserver)
                      );

            bot.StartReceiving(
                            updateHandler: TgHandleUpdateAsync,
                            pollingErrorHandler: TgHandlePollingErrorAsync,
                            receiverOptions: new ReceiverOptions { AllowedUpdates = Array.Empty<UpdateType>() },
                            cancellationToken: CancellationToken.None
                      );

            _ = Task.Run(() => bot.GetMeAsync());

            Log("application", "", "", "Telegram bot polling started");

            WebUi.Start(fs);

            if (settings.ai != null &&
                !string.IsNullOrEmpty(settings.ai.url) &&
                !string.IsNullOrEmpty(settings.ai.model))
            {
                aiQueue = new AIQueueService(bot, settings.ai);
                aiQueue.Start();
                goAI = true;
            }
            else
            {
                Log("application", "", "", "AI service not configured, skipping");
            }


            try
            {
                mqttFactory = new MqttFactory();
                mqttClient = mqttFactory.CreateMqttClient();
                mqttOptions = new MqttClientOptionsBuilder()
                    .WithClientId("frte2tg")
                    .WithTcpServer(settings.mqtt.host, settings.mqtt.port)
                    .WithCredentials(settings.mqtt.user, settings.mqtt.password)
                    //                .WithTls()
                    .WithCleanSession()
                    .Build();

                mqttClient.ApplicationMessageReceivedAsync += MqttClientApplicationMessageReceivedAsync;
                mqttClient.ConnectedAsync += MqttClientConnectedAsync;
                mqttClient.DisconnectedAsync += MqttClientDisconnectedAsync;

                try
                {
                    await mqttClient.ConnectAsync(mqttOptions);
                }
                catch (Exception exception)
                {
                    Log("application", "", "", "Connecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed" + exception);
                }

                Log("application", "", "", "Waiting for mqtt messages");

                var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                                            .WithTopicFilter(f => f.WithTopic(settings.mqtt.eventstopic))
                                            .WithTopicFilter(f => f.WithTopic(settings.mqtt.reviewstopic))
                                            .Build();
                await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);

            }
            catch (Exception exception)
            {
                ConsoleLog("application", "", "", exception.ToString());
            }

            Thread.Sleep(Timeout.Infinite);
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
                int firstmessageid = -1;

                List<string> rulabels = new List<string>();
                foreach (var ob in fr.after.data.objects)
                {
                    rulabels.Add(((ob.ToLower() != null || ob.ToLower() != "") ? rumapobj[ob.ToLower()].ToString() : "что-то"));
                }

                if (settings.frigate.cameras[cami].snapshot)
                {
                    bool allfilesready = true;
                    int secs = 0;
                    while (secs <= settings.options.timeout)
                    {
                        allfilesready = true;
                        foreach (var ev in fr.after.data.detections)
                        {
                            if (!System.IO.File.Exists(settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg"))
                                allfilesready = false;
                        }
                        if (allfilesready)
                            break;

                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!allfilesready)
                    {
                        Log("review", fr.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }


                    string tgcaption = "Обзор: \t" + fr.after.id + "\n" +
                                       "Камера: " + fr.after.camera + "\n" +
                                       "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                       "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       "События: " + string.Join(", ", fr.after.data.detections) + "";
                    List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();
                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        InputMediaPhoto imp =
                             new InputMediaPhoto(new InputFileStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg"), fr.after.camera + "-" + ev + ".jpg"))
                             {
                                 Caption = (i == 1) ? tgcaption : null,
                                 ParseMode = ParseMode.Markdown
                             };

                        md.Add(imp);
                        if (i == 9)
                            break;
                        i++;
                    }

                    if (md.Count > 0)
                    {

                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {

                            Message[] msgs = await bot.SendMediaGroupAsync(chatId: chid, media: md);
                            await Task.Delay(100);
                            firstmessageid = msgs[0].MessageId;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());


                        if (goAI && settings.frigate.cameras[cami].ai)
                        {

                            aiQueue.AddToQueue(new AITask
                            {
                                ImagePaths = fr.after.data.detections
                                                .Select(ev => settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg")
                                                .ToList(),
                                Prompt = fr.after.data.objects.Contains("person") ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
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

            }
            catch
            {
                Log("review", fr.after.id, fr.after.camera, "Error while review new/update worked");
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
                                                        List<DbRow> dl)
        {
            var parts = BuildFfmpegParts(fr.after.id, camera, dl);
            int partid = parts.Count;

            if (string.IsNullOrEmpty(tgcaption))
                tgcaption = "Обзор: \t" + fr.after.id + "\n" +
                            "Камера: " + fr.after.camera + "\n" +
                            "Объекты: " + string.Join(", ", rulabels) + "\n" +
                            "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                            "События: " + string.Join(", ", fr.after.data.detections);

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
                            await bot.SendAnimationAsync(
                                        chatId: chid,
                                        animation: InputFile.FromStream(System.IO.File.OpenRead(gifPath), Path.GetFileName(gifPath)),
                                        caption: tgcaption,
                                        replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                            await Task.Delay(100);
                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
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
                Thread.Sleep(settings.options.retry * 100);

                if (settings.frigate.cameras[cami].sctogether && settings.frigate.cameras[cami].snapshot)
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        if (!firstmessage)
                        {
                            InputMediaVideo ivp = new InputMediaVideo(
                                new InputFileStream(System.IO.File.OpenRead(parts[i - 1].path),
                                fr.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4"));
                            md.Add(ivp);

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                                    Message[] msgs = await bot.SendMediaGroupAsync(chatId: chid, media: md);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("review", fr.after.id, camera, "The snapshot and clip were sent to telegram chat " + chid);
                                    x++;

                                    if (goAI && settings.frigate.cameras[cami].ai)
                                        aiQueue.AddToQueue(new AITask
                                        {
                                            ImagePaths = fr.after.data.detections
                                                .Select(ev => settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg")
                                                .ToList(),
                                            Prompt = fr.after.data.objects.Contains("person") ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
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
                            string cap = fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео";
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                await bot.SendVideoAsync(
                                    chatId: chid,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                                await Task.Delay(100);
                                if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
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
                                ? fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео"
                                : fr.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео\n" +
                                  "Камера: " + fr.after.camera + "\n" +
                                  "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                  "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                  "События: " + string.Join(", ", fr.after.data.detections);

                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                            await bot.SendVideoAsync(
                                chatId: chid,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
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
                        rulabels.Add(rumapobj.TryGetValue(ob.ToLower(), out var label) ? label : "что-то");

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if (settings.frigate.cameras[cami].snapshot && settings.frigate.cameras[cami].snapshottrigger == "end")
                {
                    tgcaption = "Обзор: \t" + fr.after.id + "\n" +
                                "Камера: " + fr.after.camera + "\n" +
                                "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                "События: " + string.Join(", ", fr.after.data.detections);

                    int i = 1;
                    foreach (var ev in fr.after.data.detections)
                    {
                        md.Add(new InputMediaPhoto(
                            new InputFileStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg"), fr.after.camera + "-" + ev + ".jpg"))
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
                            Message[] msgs = await bot.SendMediaGroupAsync(chatId: chid, media: md);
                            await Task.Delay(100);
                            firstmessages[chid] = msgs[0].MessageId;
                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 * md.Count + 1);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                        }

                        if (goAI && settings.frigate.cameras[cami].ai)
                            foreach (var chid in settings.telegram.chatids)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = fr.after.data.detections
                                        .Select(ev => settings.frigate.clipspath + "/" + fr.after.camera + "-" + ev + ".jpg")
                                        .ToList(),
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
                    string sqlq = new Queries().getEventQuery(fr.after.id, fr.after.camera, "review", true);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                    bool isSuccess = false;

                    while (secs <= settings.options.timeout)
                    {
                        SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                        db.Open();
                        SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                        if (dr.HasRows)
                        {
                            isSuccess = true;
                            Log("review", fr.after.id, camera, "All recordings are ready");

                            if (settings.frigate.cameras[cami].trueend)
                            {
                                var fes = fr;
                                fes.type = "trueend";
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
                            Thread.Sleep(10);

                            await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl);
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
                            SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = new Queries().getEventQuery(fr.after.id, fr.after.camera, "review", false);
                            db.Open();
                            SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("review", fr.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = fr;
                                    fes.type = "trueend";
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
                                Thread.Sleep(10);

                                await SendReviewMediaAsync(fr, camera, cami, rulabels, md, firstmessages, firstmessage, tgcaption, dl);
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
                int firstmessageid = -1;
                string rulabel = ((fe.after.label.ToLower() != null || fe.after.label.ToLower() != "") ? rumapobj[fe.after.label.ToLower()].ToString() : "что-то")
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                if ((fe.after.has_snapshot) && (settings.frigate.cameras[cami].snapshot) /*&& (!settings.telegram.clip)*/)
                {
                    bool allfilesready = true;
                    int secs = 0;
                    while (secs <= settings.options.timeout)
                    {
                        allfilesready = true;
                        if (!System.IO.File.Exists(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg"))
                            allfilesready = false;
                        if (allfilesready)
                            break;
                        secs += settings.options.retry;
                        await Task.Delay(settings.options.retry * 1000);
                    }

                    if (!allfilesready)
                    {
                        Log("event", fe.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    int x = 1;
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = "" + fe.after.id + " фото\n" +
                                           "Камера: " + fe.after.camera + "\n" +
                                           "Объект: " + rulabel + "\n" +
                                           "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                           "Событие: " + fe.after.id + "";

                        var tgTask = Task.Run(() =>
                               bot.SendPhotoAsync(
                                                chatId: chid,
                                                photo: InputFile.FromStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg")),
                                                caption: tgcaption,
                                                parseMode: ParseMode.Markdown));
                        Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                        tgTask.Wait();
                        if (x > 1)
                            Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                        x++;
                        firstmessageid = tgTask.Result.MessageId;
                        tgTask.Dispose();

                        if (goAI && settings.frigate.cameras[cami].ai)
                        {

                            aiQueue.AddToQueue(new AITask
                            {
                                ImagePaths = new List<string> { settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg" },
                                Prompt = fe.after.label == "person" ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
                                ChatId = long.Parse(chid),
                                MessageId = firstmessageid,
                                Camera = camera,
                                EventId = fe.after.id,
                                OriginalCaption = tgcaption
                            });
                        }

                        Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                    }
                }

            }
            catch
            {
                Log("event", fe.after.id, fe.after.camera, "Error in event new/update worker");
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
                tgcaption = "Событие: \t" + fe.after.id + "\n" +
                            "Камера: " + fe.after.camera + "\n" +
                            "Объект: " + rulabel + "\n" +
                            "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                            (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

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
                            await bot.SendAnimationAsync(
                                    chatId: chid,
                                    animation: InputFile.FromStream(System.IO.File.OpenRead(gifPath), Path.GetFileName(gifPath)),
                                    caption: tgcaption,
                                    replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                            await Task.Delay(100);
                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
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
                Thread.Sleep(settings.options.retry * 100);

                if (settings.frigate.cameras[cami].sctogether && settings.frigate.cameras[cami].snapshot)
                {
                    for (int i = 1; i <= partid; i++)
                    {
                        if (!firstmessage)
                        {
                            InputMediaVideo ivp = new InputMediaVideo(
                                new InputFileStream(System.IO.File.OpenRead(parts[i - 1].path),
                                fe.after.id + ((partid == 1) ? "" : "-part" + i) + ".mp4"));
                            md.Add(ivp);

                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                            {
                                int x = 1;
                                foreach (var chid in settings.telegram.chatids)
                                {
                                    if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                                    Message[] msgs = await bot.SendMediaGroupAsync(chatId: chid, media: md);
                                    await Task.Delay(100);
                                    firstmessages[chid] = msgs[0].MessageId;
                                    Log("event", fe.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid);
                                    x++;

                                    if (goAI && settings.frigate.cameras[cami].ai)
                                        aiQueue.AddToQueue(new AITask
                                        {
                                            ImagePaths = new List<string> { settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg" },
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
                        else
                        {
                            string cap = fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео";
                            int x = 1;
                            foreach (var chid in settings.telegram.chatids)
                            {
                                if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                                await bot.SendVideoAsync(
                                    chatId: chid,
                                    video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                    caption: cap,
                                    supportsStreaming: true,
                                    parseMode: ParseMode.Markdown,
                                    replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
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
                                ? fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео"
                                : fe.after.id + ((partid == 1) ? "" : "[" + i + "]") + " видео\n" +
                                  "Камера: " + fe.after.camera + "\n" +
                                  "Объект: " + rulabel + "\n" +
                                  "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                  (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 + 1);
                            await bot.SendVideoAsync(
                                chatId: chid,
                                video: InputFile.FromStream(System.IO.File.OpenRead(parts[i - 1].path)),
                                caption: cap,
                                supportsStreaming: true,
                                parseMode: ParseMode.Markdown,
                                replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
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

                string rulabel = (rumapobj.TryGetValue(fe.after.label.ToLower(), out var rl) ? rl : "что-то")
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if (settings.frigate.cameras[cami].snapshot && settings.frigate.cameras[cami].snapshottrigger == "end")
                {
                    tgcaption = "Обзор: \t" + fe.after.id + "\n" +
                                "Камера: " + fe.after.camera + "\n" +
                                "Объект: " + rulabel + "\n" +
                                "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "");

                    md.Add(new InputMediaPhoto(
                        new InputFileStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg"), fe.after.camera + "-" + fe.after.id + ".jpg"))
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
                            Message[] msgs = await bot.SendMediaGroupAsync(chatId: chid, media: md);
                            await Task.Delay(100);
                            firstmessages[chid] = msgs[0].MessageId;
                            if (x > 1) Thread.Sleep(settings.telegram.sendchatstimepause * 1000 * md.Count + 1);
                            x++;
                            Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid);
                        }

                        if (goAI && settings.frigate.cameras[cami].ai)
                            foreach (var chid in settings.telegram.chatids)
                                aiQueue.AddToQueue(new AITask
                                {
                                    ImagePaths = new List<string> { settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg" },
                                    Prompt = fe.after.label == "person" ? settings.ai.humanprompt : settings.ai.nonhumanprompt,
                                    ChatId = long.Parse(chid),
                                    MessageId = firstmessages[chid],
                                    Camera = camera,
                                    EventId = fe.after.id,
                                    OriginalCaption = tgcaption
                                });
                    }
                }

                if (fe.after.has_clip && (settings.frigate.cameras[cami].clip || settings.frigate.cameras[cami].gif))
                {
                    int secs = 0;
                    string sqlq = new Queries().getEventQuery(fe.after.id, fe.after.camera, "event", true);
                    SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
                    bool isSuccess = false;

                    while (secs <= settings.options.timeout)
                    {
                        SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                        db.Open();
                        SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                        if (dr.HasRows)
                        {
                            isSuccess = true;
                            Log("event", fe.after.id, camera, "All recordings are ready");

                            if (settings.frigate.cameras[cami].trueend)
                            {
                                var fes = fe;
                                fes.type = "trueend";
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
                            Thread.Sleep(10);

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
                            SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = new Queries().getEventQuery(fe.after.id, fe.after.camera, "event", false);
                            db.Open();
                            SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                            if (dr.HasRows)
                            {
                                Log("event", fe.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = fe;
                                    fes.type = "trueend";
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
                                Thread.Sleep(10);

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

                var payloadText = string.Empty;
                if (arg.ApplicationMessage.PayloadSegment.Count > 0)
                {
                    payloadText = Encoding.UTF8.GetString(
                        arg.ApplicationMessage.PayloadSegment.Array,
                        arg.ApplicationMessage.PayloadSegment.Offset,
                        arg.ApplicationMessage.PayloadSegment.Count);
                }

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
                        Log("application", "", "", "Bad payload");
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fe.after.camera);

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

                        if ((settings.frigate.cameras[cami].objects.Count() == 0) ||
                                (
                                    !(settings.frigate.cameras[cami].objects.Count() > 0) &&
                                    (settings.frigate.cameras[cami].objects.Select(x => x.label).ToList().Contains(fe.after.label)) &&
                                    (settings.frigate.cameras[cami].objects[settings.frigate.cameras[cami].objects.FindIndex(m => m.label == fe.after.label)].percent >= fe.after.score)
                                )
                           )
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

                        if ((settings.frigate.cameras[cami].objects.Count() == 0) ||
                                (
                                    (settings.frigate.cameras[cami].objects.Count() > 0) &&
                                    (settings.frigate.cameras[cami].objects.Select(x => x.label).ToList().Contains(fe.after.label)) &&
                                    (settings.frigate.cameras[cami].objects[settings.frigate.cameras[cami].objects.FindIndex(m => m.label == fe.after.label)].percent >= fe.after.score)
                                )
                           )
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
                        Log("application", "", "", "Bad payload");
                        return;
                    }

                    int cami = settings.frigate.cameras.FindIndex(m => m.camera == fr.after.camera);

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


                        if ((settings.frigate.cameras[cami].objects.Count() == 0) ||
                                (
                                    (settings.frigate.cameras[cami].objects.Count() > 0) &&
                                    (settings.frigate.cameras[cami].objects.Select(x => x.label).ToList().Intersect(fr.after.data.objects).Count() > 0)
                                )
                           )
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

                        if ((settings.frigate.cameras[cami].objects.Count() == 0) ||
                                (
                                    (settings.frigate.cameras[cami].objects.Count() > 0) &&
                                    (settings.frigate.cameras[cami].objects.Select(x => x.label).ToList().Intersect(fr.after.data.objects).Count() > 0)
                                )
                           )
                        {
                            Log("review", fr.after.id, fr.after.camera, "Review new/update received");
                            _ = Task.Run(() => FrigateReviewNewWorker(fr: fr));
                        }
                    }
                }

                return;
            }

            async static Task MqttClientConnectedAsync(MqttClientConnectedEventArgs arg)
            {
                Log("application", "", "", "Connected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
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
                Log("application", "", "", "Subscribed to topics " + settings.mqtt.eventstopic + ", " + settings.mqtt.reviewstopic);
                return;
            }

            async static Task MqttClientDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
            {
                Log("application", "", "", "Disconnected from mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
                await Task.Delay(TimeSpan.FromSeconds(5));

                try
                {
                    await mqttClient.ConnectAsync(mqttOptions);
                }
                catch
                {
                    Log("application", "", "", "Reconnecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed");
                }

                return;
            }

            async static Task DownloadFileAsync(string url, string filename)
            {
                try
                {
                    var data = await new WebClient().DownloadDataTaskAsync(new Uri(url));
                    await new FileStream(filename, FileMode.Create).WriteAsync(data, 0, data.Length);
                }
                catch (Exception)
                {
                    Log("application", "", "", "Failed to download File: " + url);
                }
                return;
            }

            async static Task TgHandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
            {

                if (update.Message is not { } message)
                    return;
                if (message.Text is not { } messageText)
                    return;

                if (!settings.telegram.chatids.Contains(message.Chat.Id.ToString()))
                {
                    await botClient.SendTextMessageAsync(chatId: message.Chat.Id, text: "go away!", cancellationToken: cancellationToken);
                    return;
                }

                if (messageText.ToLower().Contains("/private"))
                {

                }

                if (messageText.ToLower().Contains("/status"))
                {
                    SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                    db.Open();
                    SqliteDataReader dr = (new SqliteCommand(new Queries().getCamerasQuery(), db)).ExecuteReader();
                    List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                    if (dr.HasRows)
                    {
                        int i = 1;

                        while (dr.Read())
                        {
                            string rnd = RandomString(10);
                            await Task.WhenAll(DownloadFileAsync("http://" + settings.frigate.host + ":" + settings.frigate.port.ToString() + "/api/" + dr["camera"].ToString() + "/latest.jpg",
                                                                 appLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg")
                                              );
                            InputMediaPhoto imp =
                                 new InputMediaPhoto(new InputFileStream(System.IO.File.OpenRead(appLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg"), dr["camera"].ToString() + "_" + rnd + ".jpg"))
                                 {
                                     Caption = ((i == 1) || (i % 11 == 0)) ? "Текущая обстановка" : null
                                 };

                            md.Add(imp);
                            if (i % 10 == 0)
                            {
                                await Task.WhenAll(botClient.SendMediaGroupAsync(chatId: message.Chat.Id, media: md));
                                md.Clear();
                            }
                            System.IO.File.Delete(appLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg");
                            i++;

                        }
                    }
                    dr.Close();
                    db.Close();
                    if (md.Count > 0)
                        await Task.WhenAll(botClient.SendMediaGroupAsync(chatId: message.Chat.Id, media: md));

                }

                return;
            }

            static Task TgHandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
            {
                var ErrorMessage = exception switch
                {
                    ApiRequestException
                    apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                    _ => exception.ToString()
                };

                Log("application", "", "", ErrorMessage);
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

        public static void FileLog(string type, string eventid, string camera, string txt)
        {
            //Directory.CreateDirectory(appLocation + "/logs");
            Directory.CreateDirectory("/var/log/frte2tg/");
            System.IO.File.AppendAllText(/*appLocation + "/logs/"*/ "/var/log/frte2tg/frte2tg_"
                                            + DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd") + ".log",
                                              DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt + "\n");
        }

        public static void ConsoleLog(string type, string eventid, string camera, string txt)
        {
            Console.WriteLine(DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + type + "\t" + eventid + "\t" + camera + "\t" + txt);
        }

    }

}
