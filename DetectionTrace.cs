using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace PrefireMacro
{
    internal sealed class DetectionTrace
    {
        internal const string Header = "milliseconds,enabled,held,valid,present,stable,raw_found,bounds,white_template_support,observation,gate,action,frame_app_ms,capture_ms,detect_ms,frame_age_ms,capture_backend";
        internal event Action<string> LineAdded;
        readonly Queue<string> rows = new Queue<string>();
        static string Quote(object value) { return "\"" + Convert.ToString(value, CultureInfo.InvariantCulture).Replace("\"", "\"\"") + "\""; }
        internal void Add(long now, bool enabled, bool held, BarObservation observation, string gate, string action)
        {
            string row = String.Join(",", new string[] { now.ToString(CultureInfo.InvariantCulture), enabled.ToString(), held.ToString(), observation == null ? "" : observation.Valid.ToString(), observation == null ? "" : observation.Present.ToString(), observation == null ? "" : observation.Stable.ToString(), observation == null ? "" : observation.RawFound.ToString(), Quote(observation == null ? "" : observation.Bounds.ToString()), Quote(observation == null ? "" : observation.TemplateSupport.ToString("F3", CultureInfo.InvariantCulture)), Quote(observation == null ? "" : observation.Reason), Quote(gate), Quote(action) });
            row += "," + (observation == null || observation.FrameAppMs < 0 ? ",,," :
                observation.FrameAppMs.ToString(CultureInfo.InvariantCulture) + "," + observation.CaptureMs.ToString("F2", CultureInfo.InvariantCulture) + "," +
                observation.DetectMs.ToString("F2", CultureInfo.InvariantCulture) + "," + Math.Max(0, now - observation.FrameAppMs).ToString(CultureInfo.InvariantCulture));
            row += "," + Quote(observation == null ? "" : observation.CaptureBackend);
            rows.Enqueue(row);
            while (rows.Count > 2048) rows.Dequeue();
            if (LineAdded != null) LineAdded(row);
        }
        internal string Save()
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrefireMacro", "diagnostics");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "trace-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-UTC.csv");
            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(true)))
            {
                writer.WriteLine(Header);
                foreach (string row in rows) writer.WriteLine(row);
            }
            return path;
        }
    }
}
