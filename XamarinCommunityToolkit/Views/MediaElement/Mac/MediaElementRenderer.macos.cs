using System;
using System.IO;
using AppKit;
using AVFoundation;
using AVKit;
using CoreMedia;
using Foundation;
using Xamarin.Forms;
using Xamarin.Forms.Internals;
using Xamarin.Forms.Platform.MacOS;
using ToolKitMediaElement = Xamarin.CommunityToolkit.UI.Views.MediaElement;
using ToolKitMediaElementRenderer = Xamarin.CommunityToolkit.UI.Views.MediaElementRenderer;
using XCT = Xamarin.CommunityToolkit.Core;

[assembly: ExportRenderer(typeof(ToolKitMediaElement), typeof(ToolKitMediaElementRenderer))]

namespace Xamarin.CommunityToolkit.UI.Views
{
	public class MediaElementRenderer : ViewRenderer<ToolKitMediaElement, NSView>
	{
		IMediaElementController Controller => Element;

		protected readonly AVPlayerView avPlayerView = new AVPlayerView();
		protected NSObject playedToEndObserver;
		protected NSObject statusObserver;
		protected NSObject rateObserver;
		protected NSObject volumeObserver;
		bool idleTimerDisabled = false;
		AVPlayerLayer playerLayer;

		public MediaElementRenderer() =>
			playedToEndObserver = NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, PlayedToEnd);

		protected virtual void SetKeepScreenOn(bool value) => avPlayerView.Player.PreventsDisplaySleepDuringVideoPlayback = value;

		protected virtual void UpdateSource()
		{
			if (Element.Source != null)
			{
				AVAsset asset = null;

				if (Element.Source is XCT.UriMediaSource uriSource)
				{
					if (uriSource.Uri.Scheme == "ms-appx")
					{
						if (uriSource.Uri.LocalPath.Length <= 1)
							return;

						// used for a file embedded in the application package
						asset = AVAsset.FromUrl(NSUrl.FromFilename(uriSource.Uri.LocalPath.Substring(1)));
					}
					else if (uriSource.Uri.Scheme == "ms-appdata")
					{
						var filePath = ResolveMsAppDataUri(uriSource.Uri);

						if (string.IsNullOrEmpty(filePath))
							throw new ArgumentException("Invalid Uri", "Source");

						asset = AVAsset.FromUrl(NSUrl.FromFilename(filePath));
					}
					else
						asset = AVUrlAsset.Create(NSUrl.FromString(uriSource.Uri.AbsoluteUri));
				}
				else
				{
					if (Element.Source is XCT.FileMediaSource fileSource)
						asset = AVAsset.FromUrl(NSUrl.FromFilename(fileSource.File));
				}

				var item = new AVPlayerItem(asset);
				RemoveStatusObserver();

				statusObserver = (NSObject)item.AddObserver("status", NSKeyValueObservingOptions.New, ObserveStatus);

				if (avPlayerView.Player != null)
					avPlayerView.Player.ReplaceCurrentItemWithPlayerItem(item);
				else
				{
					avPlayerView.Player = new AVPlayer(item);
					rateObserver = (NSObject)avPlayerView.Player.AddObserver("rate", NSKeyValueObservingOptions.New, ObserveRate);
					volumeObserver = (NSObject)avPlayerView.Player.AddObserver("volume", NSKeyValueObservingOptions.New, ObserveVolume);
				}

				if (Element.AutoPlay)
					Play();
			}
			else
			{
				if (Element.CurrentState == MediaElementState.Playing || Element.CurrentState == MediaElementState.Buffering)
				{
					avPlayerView.Player.Pause();
					Controller.CurrentState = MediaElementState.Stopped;
				}
			}
		}

		protected string ResolveMsAppDataUri(Uri uri)
		{
			if (uri.Scheme == "ms-appdata")
			{
				string filePath;

				if (uri.LocalPath.StartsWith("/local"))
				{
					var libraryPath = NSFileManager.DefaultManager.GetUrls(NSSearchPathDirectory.LibraryDirectory, NSSearchPathDomain.User)[0].Path;
					filePath = Path.Combine(libraryPath, uri.LocalPath.Substring(7));
				}
				else if (uri.LocalPath.StartsWith("/temp"))
					filePath = Path.Combine(Path.GetTempPath(), uri.LocalPath.Substring(6));
				else
					throw new ArgumentException("Invalid Uri", "Source");

				return filePath;
			}
			else
				throw new ArgumentException("uri");
		}

		protected void RemoveStatusObserver()
		{
			if (statusObserver != null)
				avPlayerView?.Player?.CurrentItem?.RemoveObserver(statusObserver, "status");
			statusObserver?.Dispose();
			statusObserver = null;
		}

		protected virtual void ObserveRate(NSObservedChange e)
		{
			if (Controller is object)
			{
				switch (avPlayerView.Player.Rate)
				{
					case 0.0f:
						Controller.CurrentState = MediaElementState.Paused;
						break;

					case 1.0f:
						Controller.CurrentState = MediaElementState.Playing;
						break;
				}

				Controller.Position = Position;
			}
		}

		void ObserveVolume(NSObservedChange e)
		{
			if (Controller == null)
				return;

			Controller.Volume = avPlayerView.Player.Volume;
		}

		protected void ObserveStatus(NSObservedChange e)
		{
			Controller.Volume = avPlayerView.Player.Volume;

			switch (avPlayerView.Player.Status)
			{
				case AVPlayerStatus.Failed:
					Controller.OnMediaFailed();
					break;

				case AVPlayerStatus.ReadyToPlay:
					var duration = avPlayerView.Player.CurrentItem.Duration;

					if (duration.IsIndefinite)
						Controller.Duration = TimeSpan.Zero;
					else
						Controller.Duration = TimeSpan.FromSeconds(duration.Seconds);

					Controller.VideoHeight = (int)avPlayerView.Player.CurrentItem.Asset.NaturalSize.Height;
					Controller.VideoWidth = (int)avPlayerView.Player.CurrentItem.Asset.NaturalSize.Width;
					Controller.OnMediaOpened();
					Controller.Position = Position;
					break;
			}
		}

		TimeSpan Position
		{
			get
			{
				if (avPlayerView?.Player?.CurrentTime.IsInvalid ?? true)
					return TimeSpan.Zero;

				return TimeSpan.FromSeconds(avPlayerView.Player.CurrentTime.Seconds);
			}
		}

		void PlayedToEnd(NSNotification notification)
		{
			if (Element == null)
				return;

			if (Element.IsLooping)
			{
				avPlayerView.Player?.Seek(CMTime.Zero);
				Controller.Position = Position;
				avPlayerView.Player?.Play();
			}
			else
			{
				SetKeepScreenOn(false);
				Controller.Position = Position;

				try
				{
					Device.BeginInvokeOnMainThread(Controller.OnMediaEnded);
				}
				catch (Exception e)
				{
					Log.Warning("MediaElement", $"Failed to play media to end: {e}");
				}
			}
		}

		protected override void OnElementPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			switch (e.PropertyName)
			{
				case nameof(ToolKitMediaElement.Aspect):
					if (playerLayer == null)
					{
						playerLayer = AVPlayerLayer.FromPlayer(avPlayerView.Player);
						avPlayerView.Layer = playerLayer;
					}
					playerLayer.VideoGravity = AspectToGravity(Element.Aspect);
					break;

				case nameof(ToolKitMediaElement.KeepScreenOn):
					if (!Element.KeepScreenOn)
						SetKeepScreenOn(false);
					else if (Element.CurrentState == MediaElementState.Playing)
					{
						// only toggle this on if property is set while video is already running
						SetKeepScreenOn(true);
					}
					break;

				case nameof(ToolKitMediaElement.ShowsPlaybackControls):
					avPlayerView.ShowsFullScreenToggleButton = Element.ShowsPlaybackControls;
					break;

				case nameof(ToolKitMediaElement.Source):
					UpdateSource();
					break;

				case nameof(ToolKitMediaElement.Volume):
					if (avPlayerView.Player != null)
						avPlayerView.Player.Volume = (float)Element.Volume;
					break;
			}
		}

		void MediaElementSeekRequested(object sender, SeekRequested e)
		{
			if (avPlayerView.Player?.CurrentItem == null || avPlayerView.Player.Status != AVPlayerStatus.ReadyToPlay)
				return;

			var ranges = avPlayerView.Player.CurrentItem.SeekableTimeRanges;
			var seekTo = new CMTime(Convert.ToInt64(e.Position.TotalMilliseconds), 1000);
			foreach (var v in ranges)
			{
				if (seekTo >= v.CMTimeRangeValue.Start && seekTo < (v.CMTimeRangeValue.Start + v.CMTimeRangeValue.Duration))
				{
					avPlayerView.Player.Seek(seekTo, SeekComplete);
					break;
				}
			}
		}

		protected virtual void Play()
		{
			if (avPlayerView.Player != null)
			{
				avPlayerView.Player.Play();
				Controller.CurrentState = MediaElementState.Playing;
			}

			if (Element.KeepScreenOn)
				SetKeepScreenOn(true);
		}

		void MediaElementStateRequested(object sender, StateRequested e)
		{
			switch (e.State)
			{
				case MediaElementState.Playing:
					Play();
					break;

				case MediaElementState.Paused:
					if (Element.KeepScreenOn)
						SetKeepScreenOn(false);

					if (avPlayerView.Player != null)
					{
						avPlayerView.Player.Pause();
						Controller.CurrentState = MediaElementState.Paused;
					}
					break;

				case MediaElementState.Stopped:
					if (Element.KeepScreenOn)
						SetKeepScreenOn(false);

					avPlayerView?.Player.Pause();
					avPlayerView?.Player.Seek(CMTime.Zero);
					Controller.CurrentState = MediaElementState.Stopped;

					break;
			}

			Controller.Position = Position;
		}

		static AVLayerVideoGravity AspectToGravity(Aspect aspect) =>
			aspect switch
			{
				Aspect.Fill => AVLayerVideoGravity.Resize,
				Aspect.AspectFill => AVLayerVideoGravity.ResizeAspectFill,
				_ => AVLayerVideoGravity.ResizeAspect,
			};

		void SeekComplete(bool finished)
		{
			if (finished)
				Controller.OnSeekCompleted();
		}

		void MediaElementPositionRequested(object sender, EventArgs e) => Controller.Position = Position;

		protected override void OnElementChanged(ElementChangedEventArgs<MediaElement> e)
		{
			base.OnElementChanged(e);

			if (e.OldElement != null)
			{
				e.OldElement.PropertyChanged -= OnElementPropertyChanged;
				e.OldElement.SeekRequested -= MediaElementSeekRequested;
				e.OldElement.StateRequested -= MediaElementStateRequested;
				e.OldElement.PositionRequested -= MediaElementPositionRequested;
				SetKeepScreenOn(false);

				// stop video if playing
				if (avPlayerView?.Player?.CurrentItem != null)
				{
					if (avPlayerView?.Player?.Rate > 0)
					{
						avPlayerView?.Player?.Pause();
					}
					avPlayerView?.Player?.ReplaceCurrentItemWithPlayerItem(null);
				}

				if (playedToEndObserver != null)
					NSNotificationCenter.DefaultCenter.RemoveObserver(playedToEndObserver);
				playedToEndObserver?.Dispose();
				playedToEndObserver = null;

				if (rateObserver != null)
					avPlayerView?.Player?.RemoveObserver(rateObserver, "rate");
				rateObserver?.Dispose();
				rateObserver = null;

				if (volumeObserver != null)
					avPlayerView?.Player?.RemoveObserver(volumeObserver, "volume");
				volumeObserver?.Dispose();
				volumeObserver = null;

				RemoveStatusObserver();
			}

			if (e.NewElement != null)
			{
				SetNativeControl(avPlayerView);

				Element.PropertyChanged += OnElementPropertyChanged;
				Element.SeekRequested += MediaElementSeekRequested;
				Element.StateRequested += MediaElementStateRequested;
				Element.PositionRequested += MediaElementPositionRequested;

				avPlayerView.ShowsFullScreenToggleButton = Element.ShowsPlaybackControls;

				if (Element.KeepScreenOn)
					SetKeepScreenOn(true);

				playedToEndObserver = NSNotificationCenter.DefaultCenter.AddObserver(AVPlayerItem.DidPlayToEndTimeNotification, PlayedToEnd);
				UpdateSource();
			}
		}
	}
}
