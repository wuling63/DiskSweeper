using System;
using System.Diagnostics;
using System.Windows.Forms;

namespace DiskCleaner
{
    /// <summary>
    /// 用 Windows 工作排程器（schtasks.exe）建立／刪除排程。
    /// 工作以目前使用者、一般權限執行，所以建立時不需要系統管理員。
    /// </summary>
    public static class Scheduler
    {
        public const string AutoCleanTask = @"DiskSweeper\AutoClean";
        public const string LowSpaceTask = @"DiskSweeper\LowSpaceCheck";

        private static readonly string[] DayCodes = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

        /// <summary>每週 day 的 time（HH:mm）以 /auto 執行；回傳錯誤訊息，成功回 null。</summary>
        public static string SetAutoClean(bool enabled, DayOfWeek day, string time)
        {
            if (!enabled) return Delete(AutoCleanTask);
            return Create(AutoCleanTask, "/auto", string.Format("/SC WEEKLY /D {0} /ST {1}", DayCodes[(int)day], time));
        }

        /// <summary>每天 09:00 以 /lowspace 檢查系統磁碟空間。</summary>
        public static string SetLowSpaceCheck(bool enabled)
        {
            if (!enabled) return Delete(LowSpaceTask);
            return Create(LowSpaceTask, "/lowspace", "/SC DAILY /ST 09:00");
        }

        private static string Create(string name, string argument, string schedule)
        {
            string command = string.Format("\\\"{0}\\\" {1}", Application.ExecutablePath, argument);
            return Run(string.Format("/Create /TN \"{0}\" /TR \"{1}\" {2} /F", name, command, schedule));
        }

        private static string Delete(string name)
        {
            // 本來就沒有這個工作時 schtasks 會回錯誤，當成成功。
            if (Run(string.Format("/Query /TN \"{0}\"", name)) != null) return null;
            return Run(string.Format("/Delete /TN \"{0}\" /F", name));
        }

        private static string Run(string arguments)
        {
            try
            {
                var info = new ProcessStartInfo("schtasks.exe", arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(info))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    string error = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 ? null : (error + output).Trim();
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
