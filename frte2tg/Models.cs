using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
                            where DATETIME(end_time, 'unixepoch') >= datetime('now','-1 hour') 
                            order by camera;";

        }

        public string getEventQuery(string id)
        {
            return @"SELECT 
                                            r.camera, 
                                            r.path, 
                                            cast(r.start_time as real) as start_time, 
                                            cast(r.end_time as real) as end_time, 
                                            cast(r.duration as real) as duration
                                            FROM recordings r
                                            JOIN
                                              (SELECT r.start_time,
                                                      r.camera
                                               FROM recordings r
                                               JOIN
                                                 (SELECT camera,
                                                         start_time
                                                  FROM event
                                                  WHERE id = '" + id + @"' ) e ON r.camera = e.camera
                                               AND r.start_time >= e.start_time
                                               ORDER BY r.start_time ASC
                                               LIMIT 1) rs ON cast(r.start_time as int) >= cast(rs.start_time as int)
                                            AND r.camera=rs.camera
                                            JOIN
                                              (SELECT r.end_time,
                                                      r.camera
                                               FROM recordings r
                                               JOIN
                                                 (SELECT end_time,
                                                         camera
                                                  FROM event
                                                  WHERE id = '" + id + @"') e ON r.camera = e.camera
                                               AND e.end_time <= r.end_time
                                               ORDER BY r.end_time ASC
                                               LIMIT 1) re ON cast(r.end_time as int) <= cast(re.end_time as int)
                                            AND r.camera=re.camera
                                            ORDER BY r.start_time ASC;";

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
    }

    public class Camera
    {
        public string camera { get; set; }
        public bool snapshot { get; set; }
        public bool clip { get; set; }
        public bool trueend { get; set; }       
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
        public string topic { get; set; }
    }

    public class TelegramSettings
    {
        public string token { get; set; }
        public List<string> chatids { get; set; }
        public int clipsizecheck { get; set; }
        public int clipsizesplit { get; set; }
    }
    /*
    public class ffmpegSettings
    {
        public string path { get; set; }
    }
    */
    public class Options
    {
        public int timeoffset { get; set; }
        public int timeout { get; set; }
        public int retry { get; set; }
    }

    public class LoggerSettings
    {
        public bool file { get; set; }
        public bool console { get; set; }
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
        public List<object> current_attributes { get; set; }
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

    public class dbrow
    {
        public string path { get; set; }
        public double start_time { get; set; }
        public double end_time { get; set; }
        public double duration { get; set; }
        public string realpath { get; set; }
        public Int64 size { get; set; }
    }




}
