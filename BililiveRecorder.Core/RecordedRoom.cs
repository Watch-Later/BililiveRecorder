﻿using BililiveRecorder.FlvProcessor;
using NLog;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace BililiveRecorder.Core
{
    public class RecordedRoom : IRecordedRoom
    {
        private static readonly Logger logger = LogManager.GetCurrentClassLogger();

        private int _roomid;
        private int _realRoomid;
        private string _streamerName;

        public int Roomid
        {
            get => _roomid;
            private set
            {
                if (value == _roomid) { return; }
                _roomid = value;
                TriggerPropertyChanged(nameof(Roomid));
            }
        }
        public int RealRoomid
        {
            get => _realRoomid;
            private set
            {
                if (value == _realRoomid) { return; }
                _realRoomid = value;
                TriggerPropertyChanged(nameof(RealRoomid));
            }
        }
        public string StreamerName
        {
            get => _streamerName;
            private set
            {
                if (value == _streamerName) { return; }
                _streamerName = value;
                TriggerPropertyChanged(nameof(StreamerName));
            }
        }

        public IRecordInfo RecordInfo { get; private set; }

        public bool IsMonitoring => StreamMonitor.Receiver.IsConnected;
        public bool IsRecording => !(StreamDownloadTask?.IsCompleted ?? true);

        private readonly Func<IFlvStreamProcessor> newIFlvStreamProcessor;
        public IFlvStreamProcessor Processor
        {
            get => _processor;
            private set
            {
                if (value == _processor) { return; }
                _processor = value;
                TriggerPropertyChanged(nameof(Processor));
            }
        }
        private IFlvStreamProcessor _processor;
        private ObservableCollection<IFlvClipProcessor> Clips { get; set; } = new ObservableCollection<IFlvClipProcessor>();

        public IStreamMonitor StreamMonitor { get; }
        private ISettings _settings { get; }

        private Task StartupTask = null;
        public Task StreamDownloadTask = null;
        public CancellationTokenSource cancellationTokenSource = null;


        public double DownloadSpeedKiBps
        {
            get { return _DownloadSpeedKiBps; }
            private set { if (value != _DownloadSpeedKiBps) { _DownloadSpeedKiBps = value; TriggerPropertyChanged(nameof(DownloadSpeedKiBps)); } }
        }
        private double _DownloadSpeedKiBps = 0;
        public DateTime LastUpdateDateTime { get; private set; } = DateTime.Now;
        public long LastUpdateSize { get; private set; } = 0;

        public RecordedRoom(ISettings settings,
            Func<string, IRecordInfo> newIRecordInfo,
            Func<int, IStreamMonitor> newIStreamMonitor,
            Func<IFlvStreamProcessor> newIFlvStreamProcessor,
            int roomid)
        {
            this.newIFlvStreamProcessor = newIFlvStreamProcessor;

            _settings = settings;

            Roomid = roomid;

            {
                var roomInfo = BililiveAPI.GetRoomInfo(Roomid);
                RealRoomid = roomInfo.RealRoomid;
                StreamerName = roomInfo.Username;
            }

            RecordInfo = newIRecordInfo(StreamerName);

            StreamMonitor = newIStreamMonitor(RealRoomid);
            StreamMonitor.StreamStatusChanged += StreamMonitor_StreamStatusChanged;
        }

        public bool Start()
        {
            var r = StreamMonitor.Start();
            TriggerPropertyChanged(nameof(IsMonitoring));
            return r;
        }

        public void Stop()
        {
            StreamMonitor.Stop();
            TriggerPropertyChanged(nameof(IsMonitoring));
        }

        private void StreamMonitor_StreamStatusChanged(object sender, StreamStatusChangedArgs e)
        {
            if (StartupTask?.IsCompleted ?? true)
            {
                StartupTask = _StartRecordAsync(e.type);
            }
        }

        public void StartRecord()
        {
            StreamMonitor.Check(TriggerType.Manual);
        }

        public void StopRecord()
        {
            if (cancellationTokenSource != null)
            {
                cancellationTokenSource.Cancel();
                StreamDownloadTask.Wait();
            }
        }

        private async Task _StartRecordAsync(TriggerType triggerType)
        {
            if (IsRecording)
            {
                logger.Log(RealRoomid, LogLevel.Debug, "已经在录制中了");
                return;
            }

            HttpWebRequest request = null;
            HttpWebResponse response = null;
            Stream stream = null;

            cancellationTokenSource = new CancellationTokenSource();
            var token = cancellationTokenSource.Token;
            try
            {

                string flv_path = BililiveAPI.GetPlayUrl(RealRoomid);

                request = WebRequest.CreateHttp(flv_path);
                request.Accept = "*/*";
                request.AllowAutoRedirect = true;
                request.Referer = "https://live.bilibili.com";
                request.Headers["Origin"] = "https://live.bilibili.com";
                request.UserAgent = Utils.UserAgent;

                response = await request.GetResponseAsync() as HttpWebResponse;

                if (response.StatusCode != HttpStatusCode.OK)
                {
                    logger.Log(RealRoomid, LogLevel.Info, string.Format("尝试下载直播流时服务器返回了 ({0}){1}", response.StatusCode, response.StatusDescription));
                    response.Close();
                    request = null;
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        logger.Log(RealRoomid, LogLevel.Info, "将在15秒后重试");
                        StreamMonitor.CheckAfterSeconeds(15); // TODO: 重试次数和时间
                    }
                    return;
                }
                else
                {
                    if (triggerType == TriggerType.HttpApiRecheck)
                    {
                        triggerType = TriggerType.HttpApi;
                    }

                    Processor = newIFlvStreamProcessor().Initialize(RecordInfo.GetStreamFilePath, RecordInfo.GetClipFilePath, _settings.Feature);
                    Processor.ClipLengthFuture = _settings.Clip_Future;
                    Processor.ClipLengthPast = _settings.Clip_Past;

                    stream = response.GetResponseStream();

                    StreamDownloadTask = Task.Run(async () =>
                    {
                        const int BUF_SIZE = 1024 * 8;
                        byte[] buffer = new byte[BUF_SIZE];
                        while (!token.IsCancellationRequested)
                        {
                            int bytesRead = await stream.ReadAsync(buffer, 0, BUF_SIZE, token);
                            _UpdateDownloadSpeed(bytesRead);
                            if (bytesRead == 0)
                            {
                                // 录制已结束
                                // TODO: 重试次数和时间
                                // TODO: 用户操作停止时不重新继续

                                logger.Log(RealRoomid, LogLevel.Info,
                                    (token.IsCancellationRequested ? "用户操作" : "直播已结束") + "，停止录制。"
                                    + (triggerType != TriggerType.HttpApiRecheck ? "将在15秒后重试启动。" : ""));
                                if (triggerType != TriggerType.HttpApiRecheck)
                                {
                                    StreamMonitor.CheckAfterSeconeds(15);
                                }
                                break;
                            }
                            else
                            {
                                if (bytesRead != BUF_SIZE)
                                {
                                    Processor.AddBytes(buffer.Take(bytesRead).ToArray());
                                }
                                else
                                {
                                    Processor.AddBytes(buffer);
                                }
                            }
                        } // while(true)
                        // outside of while
                        _CleanupFlvRequest();
                    });

                    TriggerPropertyChanged(nameof(IsRecording));
                }
            }
            catch (Exception ex)
            {
                _CleanupFlvRequest();
                logger.Log(RealRoomid, LogLevel.Warn, "启动直播流下载出错。" + (triggerType != TriggerType.HttpApiRecheck ? "将在15秒后重试启动。" : ""), ex);
                if (triggerType != TriggerType.HttpApiRecheck)
                {
                    StreamMonitor.CheckAfterSeconeds(15);
                }
            }
            void _CleanupFlvRequest()
            {
                if (Processor != null)
                {
                    Processor.FinallizeFile();
                    Processor.Dispose();
                    Processor = null;
                }
                request = null;
                stream?.Dispose();
                stream = null;
                response?.Dispose();
                response = null;

                DownloadSpeedKiBps = 0d;
                TriggerPropertyChanged(nameof(IsRecording));
            }
            void _UpdateDownloadSpeed(int bytesRead)
            {
                DateTime now = DateTime.Now;
                double sec = (now - LastUpdateDateTime).TotalSeconds;
                LastUpdateSize += bytesRead;
                if (sec > 1)
                {
                    var speed = LastUpdateSize / sec;
                    LastUpdateDateTime = now;
                    LastUpdateSize = 0;
                    DownloadSpeedKiBps = speed / 1024; // KiB per sec
                }
            }
        }

        // Called by API or GUI
        public void Clip()
        {
            Processor?.Clip();
        }

        public void Shutdown()
        {
            Stop();
            StopRecord();
        }

        private void CallBack_ClipFinalized(object sender, ClipFinalizedArgs e)
        {
            e.ClipProcessor.ClipFinalized -= CallBack_ClipFinalized;
            if (Clips.Remove(e.ClipProcessor))
            {
                Debug.WriteLine("Clip Finalized");
            }
            else
            {
                Debug.WriteLine("Warning! Clip Finalized but was not in Collection.");
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void TriggerPropertyChanged(string propertyName)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
