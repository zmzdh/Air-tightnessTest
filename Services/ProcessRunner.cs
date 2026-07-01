using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace AudioActuatorCanTest.Services
{
    public static class ProcessRunner
    {
        public static Task<ProcessResult> RunWithResultAsync(string exe, string args, int? timeoutMilliseconds = null)
        {
            return Task.Run(() =>
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };

                using Process p = Process.Start(psi);

                StringBuilder sb = new();
                p.OutputDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) sb.AppendLine(e.Data); };

                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                bool timedOut = false;

                if (timeoutMilliseconds.HasValue)
                {
                    if (!p.WaitForExit(timeoutMilliseconds.Value))
                    {
                        timedOut = true;
                        try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                    }
                    else
                    {
                        p.WaitForExit();
                    }
                }
                else
                {
                    p.WaitForExit();
                }

                return new ProcessResult(sb.ToString(), p.ExitCode, timedOut);
            });
        }

        public static async Task<string> RunAsync(string exe, string args, int? timeoutMilliseconds = null)
        {
            ProcessResult result = await RunWithResultAsync(exe, args, timeoutMilliseconds);
            if (result.TimedOut)
            {
                throw new TimeoutException($"Process '{exe} {args}' timed out after {timeoutMilliseconds} ms");
            }

            return result.Output;
        }
    }

    public record ProcessResult(string Output, int ExitCode, bool TimedOut)
    {
        public bool IsSuccess => !TimedOut && ExitCode == 0;
    }
}
