using Telegram.Bot.Types;

namespace frte2tg
{

    public class Queries
    {
        public string getCameraLastClipQuery(string camera)
        {
            return @"select * 
                            from recordings
                            where camera = '" + camera + "' order by end_time desc LIMIT 1;";

        }

        public string getCamerasQuery()
        {
            return @"select distinct camera
                            from recordings 
                            where DATETIME(end_time, 'unixepoch') >= datetime('now','-24 hour') 
                            order by camera;";

        }

        public string getEventQuery(string id, string camera, string entity, bool strong = true)
        {
            if (entity == "review")
                entity = "reviewsegment";

                return @"SELECT r.camera,
                                   r.path,
                                   cast(r.start_time AS real) AS start_time,
                                   cast(r.end_time AS real) AS end_time,
                                   cast(r.duration AS real) AS duration
                            FROM recordings r
                            WHERE r.start_time >=
                                (SELECT rs.start_time
                                 FROM recordings rs
                                 JOIN
                                   (SELECT camera,
                                           start_time
                                    FROM " + entity + @"
                                    WHERE id = '" + id + @"') s ON rs.camera = s.camera
                                 AND rs.start_time <= s.start_time
                                 ORDER BY rs.start_time DESC
                                 LIMIT 1)
                              AND r.end_time <=
                                (SELECT re.end_time
                                 FROM recordings re
                                 JOIN
                                   (SELECT end_time,
                                           camera
                                    FROM " + entity + @"
                                    WHERE id = '" + id + @"') e ON re.camera = e.camera
                                 AND e.end_time " + (strong ? "<= re.end_time" : ">= re.start_time") + 
                                 @" ORDER BY re.end_time " + (strong ? "ASC" : "DESC") +
                                 @" LIMIT 1)
                              AND r.camera = '" + camera + @"'
                            ORDER BY r.start_time";

        }

    }

    public class SettingsFile
    {
        public FrigateSettings frigate { get; set; }
        public MqttSettings mqtt { get; set; }
        public TelegramSettings telegram { get; set; }
        //public ffmpegSettings ffmpeg { get; set; }
        public Options options { get; set; }
        public LoggerSettings logger { get; set; }
        public AISettings ai { get; set; }
    }

    public class Objects
    {
        public string label { get; set; }
        public int percent { get; set; } = 50;

    }

    public class Camera
    {
        public string camera { get; set; }
        public bool snapshot { get; set; } = true;
        public bool clip { get; set; } = false;
        public bool gif { get; set; } = false;
        public bool trueend { get; set; } = false;
        public bool sctogether { get; set; } = false;
        public bool ai { get; set; } = false;
        public string topic { get; set; } = "reviews";
        public string snapshottrigger { get; set; } = "end";
        public List<Objects> objects { get; set; } = new List<Objects>();
        public List<string> severity { get; set; } = new List<string>() { "detection", "alert" };
        public List<string> zones { get; set; } = new List<string>();

    }

    public class FrigateSettings
    {
        public string host { get; set; }
        public int port { get; set; }
        public string clipspath { get; set; }
        public string dbpath { get; set; }
        public string recordingspath { get; set; }
        public string recordingsoriginalpath { get; set; }
        public List<Camera> cameras { get; set; } 
    }
    public class MqttSettings
    {
        public string host { get; set; }
        public int port { get; set; }
        public string user { get; set; }
        public string password { get; set; }
        public string eventstopic { get; set; } = "frigate/events";
        public string reviewstopic { get; set; } = "frigate/reviews";
    }

    public class TelegramSettings
    {
        public string token { get; set; }
        public List<string> chatids { get; set; }
        public long clipsizecheck { get; set; }
        public long clipsizesplit { get; set; }
        public int mediagrouplimit { get; set; } = 10;
        public int sendchatstimepause { get; set; } = 30;
        public string apiserver { get; set; } = "https://api.telegram.org/";
    }
    /*
    public class ffmpegSettings
    {
        public string path { get; set; }
    }
    */
    public class Options
    {
        public int timeoffset { get; set; } = 180;
        public int timeout { get; set; } = 300;
        public int retry { get; set; } = 10;
        public bool sendeverythingwhatyouhave { get; set; } = true;
        public int gifwidth { get; set; } = 640;     
    }

    public class LoggerSettings
    {
        public bool file { get; set; }
        public bool console { get; set; }
    }

    public class AISettings
    {
        public string url { get; set; }
        public string model { get; set; }
        public string humanprompt { get; set; }
        public string nonhumanprompt { get; set; }
        public int numpredict { get; set; } = 150;
        public double temperature { get; set; } = 0.1;
        public int resizetowidth { get; set; } = 640;
    }

    public class BeforeAfterFE
    {
        public string id { get; set; }
        public string camera { get; set; }
        public double frame_time { get; set; }
        public SnapshotFE snapshot { get; set; }
        public string label { get; set; }
        public object sub_label { get; set; }
        public double top_score { get; set; }
        public bool false_positive { get; set; }
        public double start_time { get; set; }
        public Double? end_time { get; set; }
        public double score { get; set; }
        public List<int> box { get; set; }
        public int area { get; set; }
        public double ratio { get; set; }
        public List<int> region { get; set; }
        public bool stationary { get; set; }
        public int motionless_count { get; set; }
        public int position_changes { get; set; }
        public List<object> current_zones { get; set; }
        public List<object> entered_zones { get; set; }
        public bool has_clip { get; set; }
        public bool has_snapshot { get; set; }
        public AttributesFE attributes { get; set; }
        public List<object> current_attributes { get; set; } = new List<object>();
    }

    public class AttributesFE
    {
    }

    public class FrigateEvent
    {
        public BeforeAfterFE before { get; set; }
        public BeforeAfterFE after { get; set; }
        public string type { get; set; }
    }

    public class SnapshotFE
    {
        public double frame_time { get; set; }
        public List<int> box { get; set; }
        public int area { get; set; }
        public List<int> region { get; set; }
        public double score { get; set; }
        public List<object> attributes { get; set; }
    }

    public class AfterBeforeReview
    {
        public string id { get; set; }
        public string camera { get; set; }
        public double start_time { get; set; }
        public Double? end_time { get; set; }
        public string severity { get; set; }
        public string thumb_path { get; set; }
        public DataReview data { get; set; }
    }

    public class DataReview
    {
        public List<string> detections { get; set; }
        public List<string> objects { get; set; }
        public List<string> sub_labels { get; set; }
        public List<string> zones { get; set; }
        public List<string> audio { get; set; }
    }

    public class FrigateReview
    {
        public string type { get; set; }
        public AfterBeforeReview before { get; set; }
        public AfterBeforeReview after { get; set; }
    }


    public class DbRow
    {
        public string path { get; set; }
        public double start_time { get; set; }
        public double end_time { get; set; }
        public double duration { get; set; }
        public string realpath { get; set; }
        public Int64 size { get; set; }
    }

    public class AITask
    {
        public List<string> ImagePaths { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public string Camera { get; set; }
        public string EventId { get; set; }
        public DateTime QueuedAt { get; set; }
        public string OriginalCaption { get; set; }
        public string Prompt { get; set; }
    }

    public class AIResponse
    {
        public int people_count { get; set; }
        public string description { get; set; }
        public string timestamp { get; set; }
    }

    public class OllamaResponse
    {
        public string response { get; set; }
    }


}
