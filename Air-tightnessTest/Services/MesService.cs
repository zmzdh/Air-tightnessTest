using System;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LumbarMassageTest.Models;

namespace LumbarMassageTest.Services
{
    public class MesService : IDisposable
    {
        private const string DefaultApiPath = "api/mes/test-records";
        private static readonly JsonSerializerOptions PayloadOptions = CreatePayloadOptions();

        private readonly HttpClient _httpClient;
        private readonly ILogService _logService;
        private bool _disposed;
        private bool _isConnected;

        public event EventHandler<bool>? OnConnectionChanged;

        public MesService(ILogService? logService = null)
        {
            _httpClient = new HttpClient();
            _logService = logService ?? LogService.Instance;
        }

        public bool IsConnected => _isConnected;

        public async Task<bool> ConnectAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MesService));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (string.IsNullOrWhiteSpace(config.MesServerIp) || config.MesServerPort <= 0)
            {
                return false;
            }

            try
            {
                if (string.Equals(config.MesProtocol, "UDP", StringComparison.OrdinalIgnoreCase))
                {
                    using var udpClient = new UdpClient();
                    udpClient.Connect(config.MesServerIp, config.MesServerPort);
                }
                else
                {
                    using var tcpClient = new TcpClient();
                    var connectTask = tcpClient.ConnectAsync(config.MesServerIp, config.MesServerPort);
                    var completedTask = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(3), cancellationToken)).ConfigureAwait(false);
                    if (completedTask != connectTask)
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            throw new OperationCanceledException(cancellationToken);
                        }

                        throw new TimeoutException("连接MES系统超时");
                    }

                    await connectTask.ConfigureAwait(false);
                }

                UpdateConnectionState(true);
                return true;
            }
            catch (OperationCanceledException)
            {
                UpdateConnectionState(false);
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError("连接MES系统失败", ex);
                UpdateConnectionState(false);
                return false;
            }
        }

        public Task DisconnectAsync()
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            UpdateConnectionState(false);
            return Task.CompletedTask;
        }

        public async Task<bool> SimulatePushAsync(AppConfig config, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MesService));
            }

            if (!_isConnected)
            {
                return false;
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            try
            {
                // 通过发送一个轻量级请求来模拟推送流程
                var heartbeat = new
                {
                    Timestamp = DateTime.UtcNow,
                    Message = "MES模拟推送"
                };

                var payload = JsonSerializer.Serialize(heartbeat, PayloadOptions);

                if (string.Equals(config.MesProtocol, "UDP", StringComparison.OrdinalIgnoreCase))
                {
                    await SendViaUdpAsync(config.MesServerIp, config.MesServerPort, payload, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(BuildMesUri(config), content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError("MES模拟推送失败", ex);
                return false;
            }
        }

        public async Task<bool> SendTestRecordAsync(TestRecord record, AppConfig config, CancellationToken cancellationToken = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(MesService));
            }

            if (record == null)
            {
                throw new ArgumentNullException(nameof(record));
            }

            if (config == null)
            {
                throw new ArgumentNullException(nameof(config));
            }

            if (string.IsNullOrWhiteSpace(config.MesServerIp) || config.MesServerPort <= 0)
            {
                return false;
            }

            string protocol = string.IsNullOrWhiteSpace(config.MesProtocol)
                ? "TCP"
                : config.MesProtocol.Trim();

            try
            {
                var payloadObject = BuildPayload(record);
                string payload = JsonSerializer.Serialize(payloadObject, PayloadOptions);

                if (string.Equals(protocol, "UDP", StringComparison.OrdinalIgnoreCase))
                {
                    await SendViaUdpAsync(config.MesServerIp, config.MesServerPort, payload, cancellationToken).ConfigureAwait(false);
                    return true;
                }

                var requestUri = BuildMesUri(config);
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(requestUri, content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logService.LogError("推送测试记录到MES失败", ex);
                return false;
            }
        }

        private static async Task SendViaUdpAsync(string host, int port, string payload, CancellationToken cancellationToken)
        {
            using var udpClient = new UdpClient();
            var data = Encoding.UTF8.GetBytes(payload);
            await udpClient.SendAsync(data, data.Length, host, port).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        private static object BuildPayload(TestRecord record)
        {
            return new
            {
                record.TestTime,
                record.WorkOrder,
                record.ProductModel,
                record.ProductCode,
                record.Operator,
                record.Channel,
                record.TestCount,
                record.TestVoltage,
                record.SleepCurrent,
                record.StaticCurrent,
                record.Result,
                record.FailReason,
                record.TestDuration,
                LumbarResults = record.LumbarResults?.Select(r => new
                {
                    r.Order,
                    r.Action,
                    r.TargetHeight,
                    r.ActualHeight,
                    r.TargetTime,
                    r.ActualTime,
                    r.PeakCurrent,
                    r.AverageCurrent,
                    r.Passed,
                    r.Message
                }).ToList(),
                MassageResults = record.MassageResults?.Select(r => new
                {
                    r.Point,
                    r.Order,
                    r.ValveOpened,
                    r.HeightSwitchTriggered,
                    r.Duration,
                    r.PeakCurrent,
                    r.AverageCurrent,
                    r.TriggerCount,
                    r.TotalHighDuration,
                    r.MaxHighDuration,
                    r.MinHighDuration,
                    r.LastHighDuration,
                    r.Passed,
                    r.Message
                }).ToList(),
                StageResults = record.StageResults?.Select(s => new
                {
                    s.Stage,
                    s.State,
                    s.StartTime,
                    s.EndTime,
                    s.Message,
                    s.PeakCurrent,
                    s.AverageCurrent,
                    s.MeasuredHeight
                }).ToList()
            };
        }

        private static Uri BuildMesUri(AppConfig config)
        {
            var builder = new UriBuilder
            {
                Scheme = "http",
                Host = config.MesServerIp,
                Port = config.MesServerPort,
                Path = DefaultApiPath
            };

            return builder.Uri;
        }

        private static JsonSerializerOptions CreatePayloadOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false
            };
            options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            return options;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _httpClient.Dispose();
            UpdateConnectionState(false);
            _disposed = true;
        }

        private void UpdateConnectionState(bool isConnected)
        {
            if (_isConnected == isConnected)
            {
                return;
            }

            _isConnected = isConnected;
            OnConnectionChanged?.Invoke(this, _isConnected);
        }
    }
}
