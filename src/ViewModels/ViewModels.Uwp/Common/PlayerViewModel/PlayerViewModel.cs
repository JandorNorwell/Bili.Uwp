﻿// Copyright (c) Richasy. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using FFmpegInterop;
using Richasy.Bili.Locator.Uwp;
using Richasy.Bili.Models.BiliBili;
using Richasy.Bili.Models.Enums;
using Richasy.Bili.ViewModels.Uwp.Common;
using Windows.Media.Playback;
using Windows.UI.Xaml.Controls;

namespace Richasy.Bili.ViewModels.Uwp
{
    /// <summary>
    /// 播放器视图模型.
    /// </summary>
    public partial class PlayerViewModel : ViewModelBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PlayerViewModel"/> class.
        /// </summary>
        public PlayerViewModel()
        {
            RelatedVideoCollection = new ObservableCollection<VideoViewModel>();
            VideoPartCollection = new ObservableCollection<VideoPartViewModel>();
            FormatCollection = new ObservableCollection<VideoFormatViewModel>();
            EpisodeCollection = new ObservableCollection<PgcEpisodeViewModel>();
            SeasonCollection = new ObservableCollection<PgcSeasonViewModel>();
            PgcSectionCollection = new ObservableCollection<PgcSectionViewModel>();
            LivePlayLineCollection = new ObservableCollection<LivePlayLineViewModel>();
            LiveQualityCollection = new ObservableCollection<LiveQualityViewModel>();
            LiveDanmakuCollection = new ObservableCollection<LiveDanmakuMessage>();
            _audioList = new List<DashItem>();
            _videoList = new List<DashItem>();
            _lastReportProgress = TimeSpan.Zero;

            _liveFFConfig = new FFmpegInteropConfig();
            _liveFFConfig.FFmpegOptions.Add("rtsp_transport", "tcp");
            _liveFFConfig.FFmpegOptions.Add("user_agent", "Mozilla/5.0 BiliDroid/1.12.0 (bbcallen@gmail.com)");
            _liveFFConfig.FFmpegOptions.Add("referer", "https://live.bilibili.com/");

            ServiceLocator.Instance.LoadService(out _numberToolkit)
                                   .LoadService(out _resourceToolkit)
                                   .LoadService(out _settingsToolkit)
                                   .LoadService(out _fileToolkit);
            PlayerDisplayMode = _settingsToolkit.ReadLocalSetting(SettingNames.DefaultPlayerDisplayMode, PlayerDisplayMode.Default);
            InitializeTimer();
            this.PropertyChanged += OnPropertyChanged;
            LiveDanmakuCollection.CollectionChanged += OnLiveDanmakuCollectionChanged;
            Controller.LiveMessageReceived += OnLiveMessageReceivedAsync;
        }

        /// <summary>
        /// 媒体播放器数据已更新.
        /// </summary>
        public event EventHandler MediaPlayerUpdated;

        /// <summary>
        /// 保存媒体控件.
        /// </summary>
        /// <param name="playerControl">播放器控件.</param>
        public void ApplyMediaControl(MediaPlayerElement playerControl)
        {
            BiliPlayer = playerControl;
        }

        /// <summary>
        /// 视频加载.
        /// </summary>
        /// <param name="vm">视图模型.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task LoadAsync(object vm)
        {
            var videoId = string.Empty;
            var seasonId = 0;

            if (vm is VideoViewModel videoVM)
            {
                videoId = videoVM.VideoId;
                _videoType = videoVM.VideoType;
            }
            else if (vm is SeasonViewModel seasonVM)
            {
                videoId = seasonVM.EpisodeId.ToString();
                seasonId = seasonVM.SeasonId;
                _videoType = VideoType.Pgc;
            }
            else if (vm is PgcSeason seasonData)
            {
                videoId = "0";
                seasonId = seasonData.SeasonId;
                _videoType = VideoType.Pgc;
            }
            else if (vm is PgcEpisodeDetail episodeData)
            {
                videoId = episodeData.Id.ToString();
                seasonId = 0;
                _videoType = VideoType.Pgc;
            }

            switch (_videoType)
            {
                case VideoType.Video:
                    await LoadVideoDetailAsync(videoId);
                    break;
                case VideoType.Pgc:
                    await LoadPgcDetailAsync(Convert.ToInt32(videoId), seasonId);
                    break;
                case VideoType.Live:
                    await LoadLiveDetailAsync(Convert.ToInt32(videoId));
                    break;
                default:
                    break;
            }

            _progressTimer.Start();
        }

        /// <summary>
        /// 改变当前分P.
        /// </summary>
        /// <param name="partId">分P Id.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task ChangeVideoPartAsync(long partId)
        {
            if (partId != 0 && VideoPartCollection.Any(p => p.Data.Page.Cid == partId))
            {
                var targetPart = VideoPartCollection.Where(p => p.Data.Page.Cid == partId).FirstOrDefault();
                CurrentVideoPart = targetPart.Data;
            }
            else
            {
                CurrentVideoPart = VideoPartCollection.First().Data;
            }

            CheckPartSelection();

            try
            {
                IsPlayInformationLoading = true;
                var play = await Controller.GetVideoPlayInformationAsync(_videoId, Convert.ToInt64(CurrentVideoPart?.Page.Cid));
                if (play != null)
                {
                    _dashInformation = play;
                }
            }
            catch (Exception ex)
            {
                IsPlayInformationError = true;
                PlayInformationErrorText = _resourceToolkit.GetLocaleString(LanguageNames.RequestVideoFailed) + $"\n{ex.Message}";
            }

            IsPlayInformationLoading = false;

            if (_dashInformation != null)
            {
                ClearPlayer();
                await InitializeVideoPlayInformationAsync(_dashInformation);
                await DanmakuViewModel.Instance.LoadAsync(_videoDetail.Arc.Aid, CurrentVideoPart.Page.Cid);
                ViewerCount = await Controller.GetOnlineViewerCountAsync(Convert.ToInt32(_videoDetail.Arc.Aid), Convert.ToInt32(CurrentVideoPart.Page.Cid));
            }
        }

        /// <summary>
        /// 改变PGC当前分集.
        /// </summary>
        /// <param name="episodeId">分集Id.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task ChangePgcEpisodeAsync(int episodeId)
        {
            if (episodeId != 0 && EpisodeCollection.Any(p => p.Data.Id == episodeId))
            {
                var targetPart = EpisodeCollection.Where(p => p.Data.Id == episodeId).FirstOrDefault();
                CurrentPgcEpisode = targetPart.Data;
            }
            else
            {
                if (PgcSectionCollection.Count > 0)
                {
                    foreach (var section in PgcSectionCollection)
                    {
                        var epi = section.Episodes.FirstOrDefault(p => p.Data.Id == episodeId);
                        if (epi != null)
                        {
                            CurrentPgcEpisode = epi.Data;
                        }
                    }
                }

                if (CurrentPgcEpisode == null)
                {
                    CurrentPgcEpisode = EpisodeCollection.FirstOrDefault()?.Data;
                }
            }

            if (CurrentPgcEpisode == null)
            {
                // 没有分集，弹出警告.
                return;
            }

            try
            {
                ViewerCount = await Controller.GetOnlineViewerCountAsync(CurrentPgcEpisode.Aid, CurrentPgcEpisode.PartId);
            }
            catch (Exception)
            {
            }

            EpisodeId = CurrentPgcEpisode?.Id.ToString() ?? string.Empty;
            CheckEpisodeSelection();

            try
            {
                IsPlayInformationLoading = true;
                var play = await Controller.GetPgcPlayInformationAsync(CurrentPgcEpisode.PartId, Convert.ToInt32(CurrentPgcEpisode.Report.SeasonType));
                if (play != null)
                {
                    _dashInformation = play;
                }
            }
            catch (Exception ex)
            {
                IsPlayInformationError = true;
                PlayInformationErrorText = _resourceToolkit.GetLocaleString(LanguageNames.RequestPgcFailed) + $"\n{ex.Message}";
            }

            IsPlayInformationLoading = false;

            if (_dashInformation != null)
            {
                ClearPlayer();
                await InitializeVideoPlayInformationAsync(_dashInformation);
                await DanmakuViewModel.Instance.LoadAsync(CurrentPgcEpisode.Aid, CurrentPgcEpisode.PartId);
            }
        }

        /// <summary>
        /// 修改清晰度.
        /// </summary>
        /// <param name="formatId">清晰度Id.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task ChangeFormatAsync(int formatId)
        {
            var preferCodecId = GetPreferCodecId();
            var conditionStreams = _videoList.Where(p => p.Id == formatId).ToList();
            if (conditionStreams.Count == 0)
            {
                var maxQuality = _videoList.Max(p => p.Id);
                _currentVideo = _videoList.Where(p => p.Id == maxQuality).FirstOrDefault();
            }
            else
            {
                var tempVideo = conditionStreams.Where(p => p.CodecId == preferCodecId).FirstOrDefault();
                if (tempVideo == null)
                {
                    tempVideo = conditionStreams.First();
                }

                _currentVideo = tempVideo;
            }

            CurrentFormat = FormatCollection.Where(p => p.Data.Quality == _currentVideo.Id).FirstOrDefault().Data;
            _currentAudio = _audioList.FirstOrDefault();

            CheckFormatSelection();

            await InitializeOnlineDashVideoAsync();
        }

        /// <summary>
        /// 修改直播清晰度.
        /// </summary>
        /// <param name="quality">清晰度.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task ChangeLiveQualityAsync(int quality)
        {
            var playInfo = await Controller.GetLivePlayInformationAsync(Convert.ToInt32(RoomId), quality);
            await InitializeLivePlayInformationAsync(playInfo);
        }

        /// <summary>
        /// 修改直播线路.
        /// </summary>
        /// <param name="order">线路序号.</param>
        /// <returns><see cref="Task"/>.</returns>
        public async Task ChangeLivePlayLineAsync(int order)
        {
            var playLine = LivePlayLineCollection.Where(p => p.Data.Order == order).FirstOrDefault()?.Data;
            if (playLine != null)
            {
                CurrentPlayLine = playLine;
                await InitializeLiveDashAsync(CurrentPlayLine.Url);
            }
        }

        /// <summary>
        /// 清理播放数据.
        /// </summary>
        public void ClearPlayer()
        {
            BiliPlayer.SetMediaPlayer(null);

            if (_currentVideoPlayer != null)
            {
                if (_currentVideoPlayer.PlaybackSession.CanPause)
                {
                    _currentVideoPlayer.Pause();
                }

                if (_currentVideoPlayer.Source != null)
                {
                    (_currentVideoPlayer.Source as MediaPlaybackItem).Source.Dispose();
                }

                _currentVideoPlayer.Dispose();
                _currentVideoPlayer = null;
            }

            _lastReportProgress = TimeSpan.Zero;
            _progressTimer.Stop();
            _heartBeatTimer.Stop();

            if (_interopMSS != null)
            {
                _interopMSS.Dispose();
                _interopMSS = null;
            }

            PlayerStatus = PlayerStatus.NotLoad;
        }

        /// <summary>
        /// 切换播放/暂停状态.
        /// </summary>
        public void TogglePlayPause()
        {
            if (_currentVideoPlayer != null)
            {
                if (PlayerStatus == PlayerStatus.Playing)
                {
                    _currentVideoPlayer.Pause();
                }
                else
                {
                    _currentVideoPlayer.Play();
                }
            }
        }

        private void OnPropertyChanged(object sender, PropertyChangedEventArgs args)
        {
            switch (args.PropertyName)
            {
                case nameof(CurrentFormat):
                    if (CurrentFormat != null)
                    {
                        _settingsToolkit.WriteLocalSetting(SettingNames.DefaultVideoFormat, CurrentFormat.Quality);
                    }

                    break;
                default:
                    break;
            }
        }

        private void OnLiveDanmakuCollectionChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            var count = LiveDanmakuCollection.Count;
            IsShowEmptyLiveMessage = count == 0;

            if (count > 0)
            {
                RequestRelatedViewScrollToBottom?.Invoke(this, EventArgs.Empty);
            }
        }
    }
}