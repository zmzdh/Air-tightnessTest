using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AudioActuatorCanTest.Services
{
    public static class PyOcdService
    {
        private const uint FlashBaseAddress = 0x10000000;

        public static async Task<ProbeQueryResult> ListProbesWithResultAsync(int? timeoutMilliseconds = null)
        {
            ProcessResult result = await ProcessRunner.RunWithResultAsync("pyocd", "list", timeoutMilliseconds);
            List<ProbeInfo> probes = ParseProbeList(result.Output);
            return new ProbeQueryResult(probes, result);
        }

        public static async Task<List<ProbeInfo>> ListProbesAsync()
        {
            ProbeQueryResult result = await ListProbesWithResultAsync();
            return result.Probes;
        }

        private static List<ProbeInfo> ParseProbeList(string output)
        {
            var list = new List<ProbeInfo>();

            foreach (var line in output.Split('\n'))
            {
                var match = Regex.Match(line,
                    @"^\s*(\d+)\s+(.+?)\s+([0-9A-F]{8,})",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    list.Add(new ProbeInfo
                    {
                        Index = int.Parse(match.Groups[1].Value),
                        Name = match.Groups[2].Value.Trim(),
                        UniqueId = match.Groups[3].Value.Trim()
                    });
                }
            }

            return list;
        }

        private static Task<ProcessResult> Cmd(string uid, string cmd, int? timeoutMilliseconds = null)
        {
            string args = $"cmd --target im941xax --uid {uid} -c \"{cmd}\"";
            return RunWithFriendlyHintsAsync(args, timeoutMilliseconds);
        }

        public static Task<ProcessResult> Halt(string uid, int? timeoutMilliseconds = null)
            => Cmd(uid, "halt", timeoutMilliseconds);

        public static Task<ProcessResult> Run(string uid, int? timeoutMilliseconds = null)
            => Cmd(uid, "go", timeoutMilliseconds);

        public static Task<ProcessResult> Reset(string uid, int? timeoutMilliseconds = null)
            => Cmd(uid, "reset", timeoutMilliseconds);

        public static Task<ProcessResult> ReadIdCode(string uid, int? timeoutMilliseconds = null)
            => Cmd(uid, "read32 0xE000ED00", timeoutMilliseconds);

        public static Task<ProcessResult> ReadMem32(string uid, uint addr, int count, int? timeoutMilliseconds = null)
            => Cmd(uid, $"read32 0x{addr:X8} {count}", timeoutMilliseconds);

        public static Task<ProcessResult> WriteMem32(string uid, uint addr, uint data, int? timeoutMilliseconds = null)
            => Cmd(uid, $"write32 0x{addr:X8} 0x{data:X8}", timeoutMilliseconds);

        public static Task<ProcessResult> FlashHex(string uid, string path, int? timeoutMilliseconds = null)
        {
            string args = $"flash --target im941xax --uid {uid} --base-address 0x{FlashBaseAddress:X8} \"{path}\"";
            return RunWithFriendlyHintsAsync(args, timeoutMilliseconds);
        }

        public static Task<ProcessResult> VerifyHex(string uid, string path, int? timeoutMilliseconds = null)
        {
            string args = $"flash --target im941xax --uid {uid} --verify --base-address 0x{FlashBaseAddress:X8} \"{path}\"";
            return RunWithFriendlyHintsAsync(args, timeoutMilliseconds);
        }

        public static Task<ProcessResult> EraseChip(string uid, int? timeoutMilliseconds = null)
        {
            string args = $"erase --target im941xax --uid {uid} --chip";
            return RunWithFriendlyHintsAsync(args, timeoutMilliseconds);
        }

        private static async Task<ProcessResult> RunWithFriendlyHintsAsync(string args, int? timeoutMilliseconds)
        {
            ProcessResult result = await ProcessRunner.RunWithResultAsync("pyocd", args, timeoutMilliseconds).ConfigureAwait(false);

            if (!result.IsSuccess && ContainsEraseFailureHint(result.Output))
            {
                string withHint = result.Output.TrimEnd() + "\n提示：擦除失败，常见原因是芯片写保护或复位脚被外部拉低。";
                result = result with { Output = withHint };
            }

            return result;
        }

        private static bool ContainsEraseFailureHint(string output)
        {
            return !string.IsNullOrWhiteSpace(output)
                && output.IndexOf("flash erase all failure", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
