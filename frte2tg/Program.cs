using System.Text;
using System.Reflection;
using System.Net;
using Microsoft.Data.Sqlite;//Microsoft.EntityFrameworkCore.Sqlite
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Protocol;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Polling;
using Telegram.Bot.Exceptions;
using Newtonsoft.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using System.Collections.Generic;

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
        public static string AppLocation;

        static async Task Main(string[] args)
        {

            AppLocation = Path.GetDirectoryName(Assembly.GetEntryAssembly().Location);
            settings = new SettingsFile();
            string fs;
            if (args.Length == 0)
                fs = "/etc/frte2tg" 
                    //AppLocation 
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
                ConsoleLog("app", "Bad settings file. Exit");
                return;
            }
            
            bot = new TelegramBotClient(settings.telegram.token);
            ReceiverOptions tgreceiverOptions = new()
            {
                AllowedUpdates = Array.Empty<UpdateType>()
            };
            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = { },
            };
            bot.StartReceiving(
                updateHandler: TgHandleUpdateAsync,
                pollingErrorHandler: TgHandlePollingErrorAsync,
                receiverOptions: tgreceiverOptions,
                cancellationToken: CancellationToken.None
            );
            Task.Run(() => bot.GetMeAsync());

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
                    Log("app", "Connecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed" + exception);
                }

                Log("app", "Waiting for mqtt messages");
                await mqttClient.SubscribeAsync(settings.mqtt.topic);
                
            }
            catch (Exception exception)
            {
                ConsoleLog("app", exception.ToString());
            }

            Thread.Sleep(Timeout.Infinite);
        }

        async static Task FrigateEventWorker(FrigateEvent fe)
        {
            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log(fe.after.id, "Start event worker");
                int firstmessageid = -1;
                string rulabel = ((fe.after.label.ToLower() != null || fe.after.label.ToLower() != "") ? rumapobj[fe.after.label.ToLower()].ToString() : "что-то")
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                if ((fe.after.has_snapshot) && (settings.frigate.cameras[cami].snapshot) /*&& (!settings.telegram.clip)*/)
                {
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = "```" + fe.after.id + " фото\n" +
                                           "Камера: " + fe.after.camera + "\n" +
                                           "Объект: " + rulabel + "\n" +
                                           "Время: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           "Событие: " + fe.after.id + "```";

                        var tgTask = Task.Run(() =>
                               bot.SendPhotoAsync(
                                                chatId: chid,
                                                photo: InputFile.FromStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg")),
                                                caption: tgcaption,
                                                parseMode: ParseMode.Markdown));
                        Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                        tgTask.Wait();
                        firstmessageid = tgTask.Result.MessageId;
                        tgTask.Dispose();
                        Log(fe.after.id, "The snapshot was sent to telegram chat " + chid.ToString());
                    }
                }

                if (fe.after.has_clip)
                {
                    int secs = 0;
                    string sqlq = new Queries().getEventQuery(fe.after.id, true);
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
                            Log(fe.after.id, "All recordings are ready");

                            if (settings.frigate.cameras[cami].trueend)
                            {
                                var fes = new FrigateEvent();
                                fes = fe;
                                fes.type = "trueend";
                                Log(fe.after.id, "Sending the trueend event");
                                var message = new MqttApplicationMessageBuilder()
                                                .WithTopic(settings.mqtt.topic)
                                                .WithPayload(JsonConvert.SerializeObject(fes))
                                                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                                .WithRetainFlag()
                                                .Build();
                                await mqttClient.PublishAsync(message, CancellationToken.None);
                            }

                            if (settings.frigate.cameras[cami].clip)
                            {

                                List<dbrow> dl = new List<dbrow>();
                                //double clipduration = 0;
                                long clipsize = 0;
                                while (dr.Read())
                                {
                                    dl.Add(new dbrow
                                    {
                                        path = (string)dr["path"],
                                        start_time = (double)dr["start_time"],
                                        end_time = (double)dr["end_time"],
                                        duration = (double)dr["duration"],
                                        realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                        size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                    });

                                    clipsize += dl.Last().size;
                                    //clipduration += (double)dr["duration"];
                                }

                                db.Close();
                                Thread.Sleep(10);

                                //недокументированный метод! для мемо
                                /*
                                
                                await Task.WhenAll(DownloadFileAsync("http://" + settings.frigate.host + ":" + settings.frigate.port.ToString() + "/api/"
                                                                            + dr["camera"].ToString()
                                                                            + "/start/" + dl.MinBy(x => x.start_time).start_time.ToString()
                                                                            + "/end/" + dl.MaxBy(x => x.end_time).end_time.ToString()
                                                                            + "/clip.mp4",
                                                                     AppLocation + "/" + fe.after.id + ".mp4"));
                                */

                                int partid = 1;
                                if (clipsize > settings.telegram.clipsizecheck)
                                {
                                    Log(fe.after.id, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
                                    int endlid = 0;
                                    int startlid = 0;
                                    double currsize = 0;

                                    for (int j = 0; j < dl.Count; j++)
                                    {
                                        if (currsize + dl[j].size <= settings.telegram.clipsizesplit)
                                        {
                                            endlid = j;
                                            currsize += dl[j].size;
                                        }
                                        else
                                        {
                                            List<string> lines = new List<string>();

                                            for (int k = startlid; k <= endlid; k++)
                                            {
                                                lines.Add("file '" + dl[k].realpath + "'");
                                            }

                                            System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
                                            var process = System.Diagnostics.Process.Start("ffmpeg" /*settings.ffmpeg.path*/, strCmdText);
                                            process.WaitForExit();
                                            process.Kill();
                                            startlid = endlid + 1;
                                            currsize = 0;
                                            partid++;
                                        }

                                    }
                                    if (currsize != 0)
                                    {
                                        List<string> lines = new List<string>();

                                        for (int k = startlid; k <= endlid; k++)
                                        {
                                            lines.Add("file '" + dl[k].realpath + "'");
                                        }

                                        System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                            + "-c copy \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
                                        var process = System.Diagnostics.Process.Start("ffmpeg" /*settings.ffmpeg.path*/, strCmdText);
                                        process.WaitForExit();
                                        process.Kill();
                                    }
                                }
                                else
                                {

                                    List<string> lines = new List<string>();
                                    for (int k = 0; k < dl.Count; k++)
                                    {
                                        lines.Add("file '" + dl[k].realpath + "'");
                                    }
                                    System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + ".txt", lines);
                                    string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                        + "-i \"" + AppLocation + "/" + fe.after.id + ".txt" + "\" "
                                                        + "-c copy \"" + AppLocation + "/" + fe.after.id + ".mp4" + "\"";
                                    var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                    process.WaitForExit();
                                    process.Kill();

                                }

                                Log(fe.after.id, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                Thread.Sleep(settings.options.retry * 100);

                                foreach (var chid in settings.telegram.chatids)
                                {
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        string tgcaption = (firstmessageid != -1) ?
                                                           "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                           "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                           "Камера: " + fe.after.camera + "\n" +
                                                           "Объект: " + rulabel + "\n" +
                                                           "Время: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                           "Событие: " + fe.after.id + "```";

                                        await bot.SendVideoAsync(
                                                chatId: chid,
                                                video: InputFile.FromStream(System.IO.File.OpenRead(AppLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                caption: tgcaption,
                                                supportsStreaming: true,
                                                parseMode: ParseMode.Markdown,
                                                replyToMessageId: (firstmessageid != -1) ? firstmessageid : null);

                                        Thread.Sleep(100);
                                        System.IO.File.Delete(AppLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        Log(fe.after.id, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                    }
                                }

                                break;
                            }
                            else
                            {

                                isSuccess = true;
                                break;
                            }
                        }

                        secs += settings.options.retry;
                        Thread.Sleep(settings.options.retry * 1000);
                    }

                    if (!isSuccess)
                    {

                        if (settings.options.everythingwhathas)
                        {
                            Log(fe.after.id, "Timeout ended, video files were not ready. Trying to send everything the frigate has");
                            SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = new Queries().getEventQuery(fe.after.id, false);
                            db.Open();
                            SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                            if (dr.HasRows)
                            {
                                isSuccess = true;
                                Log(fe.after.id, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = new FrigateEvent();
                                    fes = fe;
                                    fes.type = "trueend";
                                    Log(fe.after.id, "Sending the trueend event");
                                    var message = new MqttApplicationMessageBuilder()
                                                    .WithTopic(settings.mqtt.topic)
                                                    .WithPayload(JsonConvert.SerializeObject(fes))
                                                    .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtMostOnce)
                                                    .WithRetainFlag()
                                                    .Build();
                                    await mqttClient.PublishAsync(message, CancellationToken.None);
                                }

                                if (settings.frigate.cameras[cami].clip)
                                {

                                    List<dbrow> dl = new List<dbrow>();
                                    //double clipduration = 0;
                                    long clipsize = 0;
                                    while (dr.Read())
                                    {
                                        dl.Add(new dbrow
                                        {
                                            path = (string)dr["path"],
                                            start_time = (double)dr["start_time"],
                                            end_time = (double)dr["end_time"],
                                            duration = (double)dr["duration"],
                                            realpath = dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath),
                                            size = (new FileInfo(dr["path"].ToString().Replace(settings.frigate.recordingsoriginalpath, settings.frigate.recordingspath))).Length
                                        });

                                        clipsize += dl.Last().size;
                                        //clipduration += (double)dr["duration"];
                                    }

                                    db.Close();
                                    Thread.Sleep(10);
                                    int partid = 1;
                                    if (clipsize > settings.telegram.clipsizecheck)
                                    {
                                        Log(fe.after.id, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
                                        int endlid = 0;
                                        int startlid = 0;
                                        double currsize = 0;

                                        for (int j = 0; j < dl.Count; j++)
                                        {
                                            if (currsize + dl[j].size <= settings.telegram.clipsizesplit)
                                            {
                                                endlid = j;
                                                currsize += dl[j].size;
                                            }
                                            else
                                            {
                                                List<string> lines = new List<string>();

                                                for (int k = startlid; k <= endlid; k++)
                                                {
                                                    lines.Add("file '" + dl[k].realpath + "'");
                                                }

                                                System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                                string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                    + "-i \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                    + "-c copy \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
                                                var process = System.Diagnostics.Process.Start("ffmpeg" /*settings.ffmpeg.path*/, strCmdText);
                                                process.WaitForExit();
                                                process.Kill();
                                                startlid = endlid + 1;
                                                currsize = 0;
                                                partid++;
                                            }

                                        }
                                        if (currsize != 0)
                                        {
                                            List<string> lines = new List<string>();

                                            for (int k = startlid; k <= endlid; k++)
                                            {
                                                lines.Add("file '" + dl[k].realpath + "'");
                                            }

                                            System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + AppLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
                                            var process = System.Diagnostics.Process.Start("ffmpeg" /*settings.ffmpeg.path*/, strCmdText);
                                            process.WaitForExit();
                                            process.Kill();
                                        }
                                    }
                                    else
                                    {

                                        List<string> lines = new List<string>();
                                        for (int k = 0; k < dl.Count; k++)
                                        {
                                            lines.Add("file '" + dl[k].realpath + "'");
                                        }
                                        System.IO.File.WriteAllLines(AppLocation + "/" + fe.after.id + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + AppLocation + "/" + fe.after.id + ".txt" + "\" "
                                                            + "-c copy \"" + AppLocation + "/" + fe.after.id + ".mp4" + "\"";
                                        var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                        process.WaitForExit();
                                        process.Kill();

                                    }

                                    Log(fe.after.id, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                    Thread.Sleep(settings.options.retry * 100);

                                    foreach (var chid in settings.telegram.chatids)
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {
                                            string tgcaption = (firstmessageid != -1) ?
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                               "Камера: " + fe.after.camera + "\n" +
                                                               "Объект: " + rulabel + "\n" +
                                                               "Время: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                               "Событие: " + fe.after.id + "```";

                                            await bot.SendVideoAsync(
                                                    chatId: chid,
                                                    video: InputFile.FromStream(System.IO.File.OpenRead(AppLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                    caption: tgcaption,
                                                    supportsStreaming: true,
                                                    parseMode: ParseMode.Markdown,
                                                    replyToMessageId: (firstmessageid != -1) ? firstmessageid : null);

                                            Thread.Sleep(100);
                                            System.IO.File.Delete(AppLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            Log(fe.after.id, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                        }
                                    }
                                }
                            }
                        }
                        else
                            Log(fe.after.id, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch
            {
                Log(fe.after.id, "Error while event worked");
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

            var fe = new FrigateEvent();
            try
            {
                fe = JsonConvert.DeserializeObject<FrigateEvent>(payloadText);
            }
            catch
            {
                Log("app", "Bad payload");
            }


            if ((fe != null) && (settings.frigate.cameras.Select(x => x.camera).ToList().Contains(fe.after.camera)) && (fe.type == "end"))
            {
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == fe.after.camera);
                if ((!settings.frigate.cameras[cami].snapshot) && (!settings.frigate.cameras[cami].clip) && (!settings.frigate.cameras[cami].trueend))
                    return;

                Log(fe.after.id, "Event end received");
                Task.Run(() => FrigateEventWorker(fe: fe));

            }
            return;
        }

        async static Task MqttClientConnectedAsync(MqttClientConnectedEventArgs arg)
        {
            Log("app", "Connected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            var mqttSubscribeOptions = mqttFactory.CreateSubscribeOptionsBuilder()
                                        .WithTopicFilter(x =>
                                            {
                                                x.WithTopic(settings.mqtt.topic);
                                            })
                                        .Build();
            await mqttClient.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
            Log("app", "Subscribed to topic " + settings.mqtt.topic);
            return;
        }

        async static Task MqttClientDisconnectedAsync(MqttClientDisconnectedEventArgs arg)
        {
            Log("app", "Disconnected from mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString());
            await Task.Delay(TimeSpan.FromSeconds(5));

            try
            {
                await mqttClient.ConnectAsync(mqttOptions);
            }
            catch
            {
                Log("app", "Reconnected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed");
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
                Log("app", "Failed to download File: " + url);
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
                await botClient.SendTextMessageAsync(chatId: message.Chat.Id.ToString(), text: "go away!", cancellationToken: cancellationToken);
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
                                                             AppLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg")
                                          );
                        InputMediaPhoto imp =  
                             new InputMediaPhoto(new InputFileStream(System.IO.File.OpenRead(AppLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg"), dr["camera"].ToString() + "_" + rnd + ".jpg"))
                                {
                                    Caption = (i == 1) ? "Текущая обстановка" : null
                                };
                        i++;
                        md.Add(imp);
                        System.IO.File.Delete(AppLocation + "/" + dr["camera"].ToString() + "_" + rnd + ".jpg");

                    }
                }
                dr.Close();
                db.Close();
                
                foreach (var chid in settings.telegram.chatids)
                {
                    await Task.WhenAll(botClient.SendMediaGroupAsync(chatId: message.Chat.Id.ToString(), media: md));
                }
            }

            return;
        }

        static Task TgHandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
        {
            var ErrorMessage = exception switch
            {
                ApiRequestException 
                apiRequestException => $"Telegram API Error: [{apiRequestException.ErrorCode}] {apiRequestException.Message}",
                _                   => exception.ToString()
            };

            Log("app", ErrorMessage);
            return Task.CompletedTask;
        }

        private static Random random = new Random();
        public static string RandomString(int length)
        {
            return new string(Enumerable.Repeat("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", length).Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static void Log(string label, string txt)
        {
            if (settings.logger.console)
                ConsoleLog(label, txt);
            if (settings.logger.file)
                FileLog(label, txt);
        }

        public static void FileLog(string label, string txt)
        {
            //Directory.CreateDirectory(AppLocation + "/logs");
            Directory.CreateDirectory("/var/log/frte2tg/");
            System.IO.File.AppendAllText(/*AppLocation + "/logs/"*/ "/var/log/frte2tg/frte2tg_"
                                            + DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd") + ".log",
                                              DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + label + "\t" + txt + "\n");
        }

        public static void ConsoleLog(string label, string txt)
        {
            Console.WriteLine(DateTime.Now.AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" + label + "\t" + txt);
        }

    }

}
