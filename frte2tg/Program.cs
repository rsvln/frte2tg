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
using System.Security.Cryptography;
using System.Collections;
using Microsoft.VisualBasic;
using System.Runtime;
using YamlDotNet.Core.Tokens;

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
        //public static string triggerType = "";
        //public static string triggerTypeRU;

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


            if (settings.telegram.apiserver != "")
            {
                TelegramBotClientOptions tbco = new TelegramBotClientOptions(
                        token: settings.telegram.token,
                        baseUrl: settings.telegram.apiserver);
                bot = new TelegramBotClient(new TelegramBotClientOptions(
                        token: settings.telegram.token,
                        baseUrl: settings.telegram.apiserver));
            }
            else
            {
                TelegramBotClientOptions tbco = new TelegramBotClientOptions(
                        token: settings.telegram.token);
                bot = new TelegramBotClient(tbco);
            }

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
                    Log("application", "", "", "Connecting to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed" + exception);
                }

                Log("application", "", "", "Waiting for mqtt messages");

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

            }
            catch (Exception exception)
            {
                ConsoleLog("application", "", "", exception.ToString());
            }

            Thread.Sleep(Timeout.Infinite);
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
                        Thread.Sleep(settings.options.retry * 1000);
                    }

                    if (!allfilesready)
                    {
                        Log("review", fr.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }



                    string tgcaption = "```Обзор: \t" + fr.after.id + "\n" +
                                       "Камера: " + fr.after.camera + "\n" +
                                       "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                       "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       "События: " + string.Join(", ", fr.after.data.detections) + "```";
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

                            var tgTask = Task.Run(() =>
                                   bot.SendMediaGroupAsync(
                                                    chatId: chid,
                                                    media: md));
                            Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                            tgTask.Wait();
                            if (x > 1)
                                Thread.Sleep(settings.telegram.sendchatstimepause*1000*md.Count + 1);
                            x++;
                            Message[] msgs = tgTask.Result;
                            firstmessageid = msgs[0].MessageId;
                            tgTask.Dispose();
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                        }
                    }


                }

            }
            catch
            {
                Log("review", fr.after.id, fr.after.camera, "Error while review new/update worked");
            }
        }


        async static Task FrigateReviewEndWorker(FrigateReview fr)
        {
            //try
            //{
                string camera = fr.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("review", fr.after.id, camera, "Start review end worker");
                //int firstmessageid = -1;
                bool firstmessage = false;
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();

                foreach (var chid in settings.telegram.chatids)
                {
                    firstmessages.Add(chid, -1);
                }

                List<string> rulabels = new List<string>();
                foreach (var ob in fr.after.data.objects)
                {
                    rulabels.Add(((ob.ToLower() != null || ob.ToLower() != "") ? rumapobj[ob.ToLower()].ToString() : "что-то"));
                }

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if ((settings.frigate.cameras[cami].snapshot) && ((settings.frigate.cameras[cami].snapshottrigger == "end")))
                {
                    string tgcaption = "```Обзор: \t" + fr.after.id + "\n" +
                                       "Камера: " + fr.after.camera + "\n" +
                                       "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                       "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       "События: " + string.Join(", ", fr.after.data.detections) + "```";

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
                        if (i == settings.telegram.mediagrouplimit - 1)
                            break;
                        i++;
                    }

                    if (
                        ((md.Count > 0) && !(settings.frigate.cameras[cami].sctogether)) ||
                        ((md.Count > 0) && !(settings.frigate.cameras[cami].clip))
                       )
                    {
                        firstmessage = true;
                        int x = 1;

                        foreach (var chid in settings.telegram.chatids)
                        {

                            var tgTask = Task.Run(() =>
                                   bot.SendMediaGroupAsync(
                                                    chatId: chid,
                                                    media: md));
                            Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                            tgTask.Wait();
                            Message[] msgs = tgTask.Result;
                            firstmessages[chid] = msgs[0].MessageId;
                            tgTask.Dispose();
                            if (x > 1)
                                Thread.Sleep(settings.telegram.sendchatstimepause*1000 * md.Count + 1);
                            x++;
                            Log("review", fr.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                        }
                    }
                }

                if (true)
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
                                var fes = new FrigateReview();
                                fes = fr;
                                fes.type = "trueend";
                                Log("review", fr.after.id, camera, "Sending the trueend review");
                                var message = new MqttApplicationMessageBuilder()
                                                .WithTopic(settings.mqtt.reviewstopic)
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
                                    Log("review", fr.after.id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
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

                                            System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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

                                        System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                            + "-c copy \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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
                                    System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + ".txt", lines);
                                    string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                        + "-i \"" + appLocation + "/" + fr.after.id + ".txt" + "\" "
                                                        + "-c copy \"" + appLocation + "/" + fr.after.id + ".mp4" + "\"";
                                    var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                    process.WaitForExit();
                                    process.Kill();

                                }

                                Log("review", fr.after.id, camera, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                Thread.Sleep(settings.options.retry * 100);


                                if ((settings.frigate.cameras[cami].sctogether) && (settings.frigate.cameras[cami].snapshot))
                                {
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        if (!firstmessage)
                                        {
                                            
                                            InputMediaVideo ivp = new InputMediaVideo(
                                                                        new InputFileStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"), fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"));
                                            md.Add(ivp);

                                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                                            {
                                                int x = 1;
                                                foreach (var chid in settings.telegram.chatids)
                                                {
                                                    if (x > 1)
                                                        Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                    var tgTask = Task.Run(() =>
                                                         bot.SendMediaGroupAsync(
                                                                chatId: chid,
                                                                media: md));

                                                    Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                                                    tgTask.Wait();
                                                    Message[] msgs = tgTask.Result;
                                                    firstmessages[chid] = msgs[0].MessageId;
                                                    tgTask.Dispose();
                                                    Log("review", fr.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid.ToString());
                                                    x++;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            string tgcaption = "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео";
                                            int x = 1;
                                            foreach (var chid in settings.telegram.chatids)
                                            {
                                                await bot.SendVideoAsync(
                                                        chatId: chid,
                                                        video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                        caption: tgcaption,
                                                        supportsStreaming: true,
                                                        parseMode: ParseMode.Markdown,
                                                        replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                                                Thread.Sleep(100);
                                                if (x > 1)
                                                    Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                                                x++;
                                                Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                            }
                                        }

                                        System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                    }
                                }
                                else
                                {
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        int x = 1;
                                        foreach (var chid in settings.telegram.chatids)
                                        {
                                            string tgcaption = (firstmessages[chid] != -1) ?
                                                               "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                               "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                               "Камера: " + fr.after.camera + "\n" +
                                                               "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                                               "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                               (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                               "События: " + string.Join(", ", fr.after.data.detections) + "```";

                                            await bot.SendVideoAsync(
                                                    chatId: chid,
                                                    video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                    caption: tgcaption,
                                                    supportsStreaming: true,
                                                    parseMode: ParseMode.Markdown,
                                                    replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);

                                            Thread.Sleep(100);
                                            if (x > 1)
                                                Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                                            x++;
                                            Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                        }
                                        System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
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

                        if (settings.options.sendeverythingwhatyouhave)
                        {
                            Log("review", fr.after.id, camera, "The timeout has ended, video files were not ready. Trying to send everything the frigate has");
                            SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = new Queries().getEventQuery(fr.after.id, fr.after.camera, "review", false);
                            db.Open();
                            SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                            if (dr.HasRows)
                            {
                                isSuccess = true;
                                Log("review", fr.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = new FrigateReview();
                                    fes = fr;
                                    fes.type = "trueend";
                                    Log("review", fr.after.id, camera, "Sending the trueend review");
                                    var message = new MqttApplicationMessageBuilder()
                                                    .WithTopic(settings.mqtt.reviewstopic)
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
                                        Log("review", fr.after.id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
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

                                                System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt", lines);
                                                string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                    + "-i \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                    + "-c copy \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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

                                            System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + appLocation + "/" + fr.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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
                                        System.IO.File.WriteAllLines(appLocation + "/" + fr.after.id + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + appLocation + "/" + fr.after.id + ".txt" + "\" "
                                                            + "-c copy \"" + appLocation + "/" + fr.after.id + ".mp4" + "\"";
                                        var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                        process.WaitForExit();
                                        process.Kill();

                                    }

                                    Log("review", fr.after.id, camera, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                    Thread.Sleep(settings.options.retry * 100);

                                    if ((settings.frigate.cameras[cami].sctogether) && (settings.frigate.cameras[cami].snapshot))
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {
                                            if (!firstmessage)
                                            {
                                                InputMediaVideo ivp = new InputMediaVideo(
                                                                            new InputFileStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"), fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"));
                                                md.Add(ivp);

                                                if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                                                {
                                                    int x = 1;
                                                    foreach (var chid in settings.telegram.chatids)
                                                    {

                                                        var tgTask = Task.Run(() =>
                                                             bot.SendMediaGroupAsync(
                                                                    chatId: chid,
                                                                    media: md));
                                                        Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                                                        tgTask.Wait();
                                                        if (x > 1)
                                                            Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                                                        Message[] msgs = tgTask.Result;
                                                        firstmessages[chid] = msgs[0].MessageId;
                                                        tgTask.Dispose();
                                                        x++;
                                                        Log("review", fr.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid.ToString());
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                string tgcaption = "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео";
                                                int x = 1;
                                                foreach (var chid in settings.telegram.chatids)
                                                {
                                                    await bot.SendVideoAsync(
                                                            chatId: chid,
                                                            video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                            caption: tgcaption,
                                                            supportsStreaming: true,
                                                            parseMode: ParseMode.Markdown,
                                                            replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                                                    Thread.Sleep(100);
                                                    if (x > 1)
                                                        Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                                                    x++;
                                                    Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                                }
                                            }

                                            System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                        }
                                    }
                                    else
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {

                                            int x = 1;
                                            foreach (var chid in settings.telegram.chatids)
                                            {
                                                string tgcaption = (firstmessages[chid] != -1) ?
                                                                   "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                                   "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                                   "Камера: " + fr.after.camera + "\n" +
                                                                   "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                                                   "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                                   (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                                   "События: " + string.Join(", ", fr.after.data.detections) + "```";

                                                if (x > 1)
                                                    Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                await bot.SendVideoAsync(
                                                        chatId: chid,
                                                        video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                        caption: tgcaption,
                                                        supportsStreaming: true,
                                                        parseMode: ParseMode.Markdown,
                                                        replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);

                                                Thread.Sleep(100);
                                                Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                                x++;
                                            }

                                            System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                        }
                                    }


                                    /*
                                    foreach (var chid in settings.telegram.chatids)
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {
                                            string tgcaption = (firstmessageid != -1) ?
                                                               "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                               "```" + fr.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                               "Камера: " + fr.after.camera + "\n" +
                                                               "Объекты: " + string.Join(", ", rulabels) + "\n" +
                                                               "Время начала: " + DateTime.UnixEpoch.AddSeconds(fr.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                               (fr.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fr.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") + 
                                                               "События: " + string.Join(", ", fr.after.data.detections) + "```";

                                            await bot.SendVideoAsync(
                                                    chatId: chid,
                                                    video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                    caption: tgcaption,
                                                    supportsStreaming: true,
                                                    parseMode: ParseMode.Markdown,
                                                    replyToMessageId: (firstmessageid != -1) ? firstmessageid : null);

                                            Thread.Sleep(100);
                                            System.IO.File.Delete(appLocation + "/" + fr.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            Log("review", fr.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                        }
                                    }
                                    */

                                }
                            }
                        }
                        else
                            Log("review", fr.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            //}
            //catch
            //{
             //   Log("review", fr.after.id, fr.after.camera, "Error while review end worked");
            //}
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
                        Thread.Sleep(settings.options.retry * 1000);
                    }

                    if (!allfilesready)
                    {
                        Log("event", fe.after.id, camera, "Error: the snapshot is not ready");
                        return;
                    }

                    int x = 1;
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = "```" + fe.after.id + " фото\n" +
                                           "Камера: " + fe.after.camera + "\n" +
                                           "Объект: " + rulabel + "\n" +
                                           "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                           "Событие: " + fe.after.id + "```";

                        var tgTask = Task.Run(() =>
                               bot.SendPhotoAsync(
                                                chatId: chid,
                                                photo: InputFile.FromStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg")),
                                                caption: tgcaption,
                                                parseMode: ParseMode.Markdown));
                        Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                        tgTask.Wait();
                        if (x > 1)
                            Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                        x++;
                        firstmessageid = tgTask.Result.MessageId;
                        tgTask.Dispose();
                        Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                    }
                }

            }
            catch
            {
                Log("event", fe.after.id, fe.after.camera, "Error while event new/update worked");
            }
        }


        async static Task FrigateEventEndWorker(FrigateEvent fe)
        {
            bool firstmessage = false;

            try
            {
                string camera = fe.after.camera;
                int cami = settings.frigate.cameras.FindIndex(m => m.camera == camera);
                Log("event", fe.after.id, camera, "Start event end worker");
                string rulabel = ((fe.after.label.ToLower() != null || fe.after.label.ToLower() != "") ? rumapobj[fe.after.label.ToLower()].ToString() : "что-то")
                               + " (" + (fe.after.score * 100).ToString("0.00") + "%)";

                /*
                if ((fe.after.has_snapshot) && (settings.frigate.cameras[cami].snapshot) && (settings.frigate.cameras[cami].snapshottrigger == "end"))
                {
                    foreach (var chid in settings.telegram.chatids)
                    {
                        string tgcaption = "```" + fe.after.id + " фото\n" +
                                           "Камера: " + fe.after.camera + "\n" +
                                           "Объект: " + rulabel + "\n" +
                                           "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                           (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
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
                        Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                    }
                }
                */

                
                Dictionary<string, int> firstmessages = new Dictionary<string, int>();
                foreach (var chid in settings.telegram.chatids)
                {
                    firstmessages.Add(chid, -1);
                }

                List<IAlbumInputMedia> md = new List<IAlbumInputMedia>();

                if ((settings.frigate.cameras[cami].snapshot) && ((settings.frigate.cameras[cami].snapshottrigger == "end")))
                {
                    string tgcaption = "```Обзор: \t" + fe.after.id + "\n" +
                                       "Камера: " + fe.after.camera + "\n" +
                                       "Объект: " + rulabel +
                                       "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                       (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                       "```";

                    InputMediaPhoto imp =
                             new InputMediaPhoto(new InputFileStream(System.IO.File.OpenRead(settings.frigate.clipspath + "/" + fe.after.camera + "-" + fe.after.id + ".jpg"), fe.after.camera + "-" + fe.after.id + ".jpg"))
                             {
                                 Caption = tgcaption,
                                 ParseMode = ParseMode.Markdown
                             };


                    if (
                        ((md.Count > 0) && !(settings.frigate.cameras[cami].sctogether)) ||
                        ((md.Count > 0) && !(settings.frigate.cameras[cami].clip))
                       )
                    {
                        firstmessage = true;
                        int x = 1;
                        foreach (var chid in settings.telegram.chatids)
                        {

                            var tgTask = Task.Run(() =>
                                   bot.SendMediaGroupAsync(
                                                    chatId: chid,
                                                    media: md));
                            Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                            tgTask.Wait();
                            Message[] msgs = tgTask.Result;
                            firstmessages[chid] = msgs[0].MessageId;
                            tgTask.Dispose();
                            if (x > 1)
                                Thread.Sleep(settings.telegram.sendchatstimepause*1000 * md.Count + 1);
                            x++;
                            Log("event", fe.after.id, camera, "The snapshot was sent to telegram chat " + chid.ToString());
                        }
                    }
                }



                if (fe.after.has_clip)
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
                                var fes = new FrigateEvent();
                                fes = fe;
                                fes.type = "trueend";
                                Log("event", fe.after.id, camera, "Sending the trueend event");
                                var message = new MqttApplicationMessageBuilder()
                                                .WithTopic(settings.mqtt.eventstopic)
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
                                                                     appLocation + "/" + fe.after.id + ".mp4"));
                                */

                                int partid = 1;
                                if (clipsize > settings.telegram.clipsizecheck)
                                {
                                    Log("event", fe.after.id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
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

                                            System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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

                                        System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                            + "-c copy \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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
                                    System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + ".txt", lines);
                                    string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                        + "-i \"" + appLocation + "/" + fe.after.id + ".txt" + "\" "
                                                        + "-c copy \"" + appLocation + "/" + fe.after.id + ".mp4" + "\"";
                                    var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                    process.WaitForExit();
                                    process.Kill();

                                }

                                Log("event", fe.after.id, camera, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                Thread.Sleep(settings.options.retry * 100);

                                ////////////////////////////


                                if ((settings.frigate.cameras[cami].sctogether) && (settings.frigate.cameras[cami].snapshot))
                                {
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        if (!firstmessage)
                                        {
                                            InputMediaVideo ivp = new InputMediaVideo(
                                                                        new InputFileStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"), fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"));
                                            md.Add(ivp);

                                            if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                                            {
                                                int x = 1;
                                                foreach (var chid in settings.telegram.chatids)
                                                {
                                                    if (x > 1)
                                                        Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                    var tgTask = Task.Run(() =>
                                                         bot.SendMediaGroupAsync(
                                                                chatId: chid,
                                                                media: md));

                                                    Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                                                    tgTask.Wait();
                                                    Message[] msgs = tgTask.Result;
                                                    firstmessages[chid] = msgs[0].MessageId;
                                                    tgTask.Dispose();
                                                    Log("review", fe.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid.ToString());
                                                    x++;
                                                }
                                            }
                                        }
                                        else
                                        {
                                            string tgcaption = "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео";
                                            int x = 1;
                                            foreach (var chid in settings.telegram.chatids)
                                            {
                                                if (x > 1)
                                                    Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                await bot.SendVideoAsync(
                                                        chatId: chid,
                                                        video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                        caption: tgcaption,
                                                        supportsStreaming: true,
                                                        parseMode: ParseMode.Markdown,
                                                        replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                                                Thread.Sleep(100);
                                                Log("review", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                                x++;
                                            }
                                        }

                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                    }
                                }
                                else
                                {
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        int x = 1;
                                        foreach (var chid in settings.telegram.chatids)
                                        {

                                            if (x > 1)
                                                Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);
                                            string tgcaption = (firstmessages[chid] != -1) ?
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                               "Камера: " + fe.after.camera + "\n" +
                                                               "Объекты: " + rulabel +
                                                               "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                               (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                               "```";

                                            await bot.SendVideoAsync(
                                                    chatId: chid,
                                                    video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                    caption: tgcaption,
                                                    supportsStreaming: true,
                                                    parseMode: ParseMode.Markdown,
                                                    replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);

                                            Thread.Sleep(100);
                                            Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                            x++;
                                        }
                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                    }
                                }

                                /*

                                for (int i = 1; i <= partid; i++)
                                {
                                    string tgcaption = (firstmessageid != -1) ?
                                                          "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                          "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                          "Камера: " + fe.after.camera + "\n" +
                                                          "Объект: " + rulabel + "\n" +
                                                          "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                          (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                          "Событие: " + fe.after.id + "```";

                                    foreach (var chid in settings.telegram.chatids)
                                    {
                                        await bot.SendVideoAsync(
                                                chatId: chid,
                                                video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                caption: tgcaption,
                                                supportsStreaming: true,
                                                parseMode: ParseMode.Markdown,
                                                replyToMessageId: (firstmessageid != -1) ? firstmessageid : null);

                                        Thread.Sleep(100);
                                        Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                    }

                                    System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                    System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                }
                                */
                                ///////////////////////

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

                        if (settings.options.sendeverythingwhatyouhave)
                        {
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready. Trying to send everything the frigate has");
                            SqliteConnection db = new SqliteConnection("Data Source = " + settings.frigate.dbpath);
                            sqlq = new Queries().getEventQuery(fe.after.id, fe.after.camera, "event", false);
                            db.Open();
                            SqliteDataReader dr = (new SqliteCommand(sqlq, db)).ExecuteReader();
                            if (dr.HasRows)
                            {
                                isSuccess = true;
                                Log("event", fe.after.id, camera, "All recordings are ready");

                                if (settings.frigate.cameras[cami].trueend)
                                {
                                    var fes = new FrigateEvent();
                                    fes = fe;
                                    fes.type = "trueend";
                                    Log("event", fe.after.id, camera, "Sending the trueend event");
                                    var message = new MqttApplicationMessageBuilder()
                                                    .WithTopic(settings.mqtt.eventstopic)
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
                                        Log("event", fe.after.id, camera, "The clip size exceeds " + settings.telegram.clipsizecheck + " bytes, will be splitted");
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

                                                System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                                string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                    + "-i \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                    + "-c copy \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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

                                            System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt", lines);
                                            string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                                + "-i \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".txt" + "\" "
                                                                + "-c copy \"" + appLocation + "/" + fe.after.id + "-part" + partid.ToString() + ".mp4" + "\"";
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
                                        System.IO.File.WriteAllLines(appLocation + "/" + fe.after.id + ".txt", lines);
                                        string strCmdText = "-y -hide_banner -loglevel error -f concat -safe 0 "
                                                            + "-i \"" + appLocation + "/" + fe.after.id + ".txt" + "\" "
                                                            + "-c copy \"" + appLocation + "/" + fe.after.id + ".mp4" + "\"";
                                        var process = System.Diagnostics.Process.Start("ffmpeg", strCmdText);
                                        process.WaitForExit();
                                        process.Kill();

                                    }

                                    Log("event", fe.after.id, camera, ((partid == 1) ? "File is prepared by ffmpeg for sending" : partid.ToString() + " files are prepared by ffmpeg for sending"));
                                    Thread.Sleep(settings.options.retry * 100);

                                    /////////////////////////////


                                    if ((settings.frigate.cameras[cami].sctogether) && (settings.frigate.cameras[cami].snapshot))
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {
                                            if (!firstmessage)
                                            {
                                                InputMediaVideo ivp = new InputMediaVideo(
                                                                            new InputFileStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"), fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4"));
                                                md.Add(ivp);

                                                if ((md.Count == settings.telegram.mediagrouplimit) || (i == partid))
                                                {
                                                    int x = 1;
                                                    foreach (var chid in settings.telegram.chatids)
                                                    {
                                                        if (x > 1)
                                                            Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                        var tgTask = Task.Run(() =>
                                                             bot.SendMediaGroupAsync(
                                                                    chatId: chid,
                                                                    media: md));

                                                        Task tgcont = tgTask.ContinueWith(x => Thread.Sleep(100));
                                                        tgTask.Wait();
                                                        Message[] msgs = tgTask.Result;
                                                        firstmessages[chid] = msgs[0].MessageId;
                                                        tgTask.Dispose();
                                                        Log("review", fe.after.id, camera, "The snapshot and clip was sent to telegram chat " + chid.ToString());
                                                        x++;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                string tgcaption = "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео";
                                                int x = 1;
                                                foreach (var chid in settings.telegram.chatids)
                                                {
                                                    if (x > 1)
                                                        Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                    await bot.SendVideoAsync(
                                                            chatId: chid,
                                                            video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                            caption: tgcaption,
                                                            supportsStreaming: true,
                                                            parseMode: ParseMode.Markdown,
                                                            replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);
                                                    Thread.Sleep(100);
                                                    Log("review", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                                    x++;
                                                }
                                            }

                                            System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                        }
                                    }
                                    else
                                    {
                                        for (int i = 1; i <= partid; i++)
                                        {
                                            int x = 1;
                                            foreach (var chid in settings.telegram.chatids)
                                            {
                                                if (x > 1)
                                                    Thread.Sleep(settings.telegram.sendchatstimepause*1000 + 1);

                                                string tgcaption = (firstmessages[chid] != -1) ?
                                                                   "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                                   "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                                   "Камера: " + fe.after.camera + "\n" +
                                                                   "Объекты: " + rulabel +
                                                                   "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                                   (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                                   "```";

                                                await bot.SendVideoAsync(
                                                        chatId: chid,
                                                        video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                        caption: tgcaption,
                                                        supportsStreaming: true,
                                                        parseMode: ParseMode.Markdown,
                                                        replyToMessageId: (firstmessages[chid] != -1) ? firstmessages[chid] : null);

                                                Thread.Sleep(100);
                                                Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                                x++;
                                            }
                                            System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                            System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                        }
                                    }


                                    /*
                                    for (int i = 1; i <= partid; i++)
                                    {
                                        string tgcaption = (firstmessageid != -1) ?
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + "``` видео" :
                                                               "```" + fe.after.id + ((partid == 1) ? "" : "[" + i.ToString() + "]") + " видео\n" +
                                                               "Камера: " + fe.after.camera + "\n" +
                                                               "Объект: " + rulabel + "\n" +
                                                               "Время начала: " + DateTime.UnixEpoch.AddSeconds(fe.after.start_time).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" +
                                                               (fe.after.end_time.HasValue ? "Время окончания: " + DateTime.UnixEpoch.AddSeconds(fe.after.end_time.Value).AddMinutes(settings.options.timeoffset).ToString("yyyy-MM-dd HH:mm:ss") + "\n" : "") +
                                                               "Событие: " + fe.after.id + "```";
                                        foreach (var chid in settings.telegram.chatids)
                                        {
                                            await bot.SendVideoAsync(
                                                    chatId: chid,
                                                    video: InputFile.FromStream(System.IO.File.OpenRead(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4")),
                                                    caption: tgcaption,
                                                    supportsStreaming: true,
                                                    parseMode: ParseMode.Markdown,
                                                    replyToMessageId: (firstmessageid != -1) ? firstmessageid : null);

                                            Thread.Sleep(100);
                                            Log("event", fe.after.id, camera, "The clip " + ((partid == 1) ? "" : "#" + i.ToString() + " ") + "was sent to telegram chat " + chid.ToString());
                                        }
                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".txt");
                                        System.IO.File.Delete(appLocation + "/" + fe.after.id + ((partid == 1) ? "" : "-part" + i.ToString()) + ".mp4");
                                    }
                                    */
                                    /////////////////////////////

                                }
                            }
                        }
                        else
                            Log("event", fe.after.id, camera, "Timeout ended, video files were not ready");
                    }
                }
            }
            catch
            {
                Log("event", fe.after.id, fe.after.camera, "Error while event end/update worked");
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
                        Task.Run(() => FrigateEventEndWorker(fe: fe));
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
                        Task.Run(() => FrigateEventNewWorker(fe: fe));
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
                        Task.Run(() => FrigateReviewEndWorker(fr: fr));
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
                        Task.Run(() => FrigateReviewNewWorker(fr: fr));
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
                Log("application", "", "", "Reconnected to mqtt server " + settings.mqtt.host + ":" + settings.mqtt.port.ToString() + " failed");
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
