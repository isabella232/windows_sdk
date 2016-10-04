﻿using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace AdjustSdk.Pcl
{
    public class ActivityHandler : IActivityHandler
    {
        private const string ActivityStateFileName = "AdjustIOActivityState";
        private const string ActivityStateName = "Activity state";
        private const string AttributionFileName = "AdjustAttribution";
        private const string AttributionName = "Attribution";

        private const string AdjustPrefix = "adjust_";

        private ILogger _Logger = AdjustFactory.Logger;
        private ActionQueue _ActionQueue = new ActionQueue("adjust.ActivityHandler");
        private InternalState _State = new InternalState();

        private DeviceUtil _DeviceUtil;
        private AdjustConfig _Config;
        private DeviceInfo _DeviceInfo;
        private ActivityState _ActivityState;
        private AdjustAttribution _Attribution;
        private TimeSpan _SessionInterval;
        private TimeSpan _SubsessionInterval;
        private IPackageHandler _PackageHandler;
        private IAttributionHandler _AttributionHandler;
        private TimerCycle _Timer;
        private object _ActivityStateLock = new object();
        private ISdkClickHandler _SdkClickHandler;

        public class InternalState
        {
            internal bool enabled;
            internal bool offline;

            public bool IsEnabled { get { return enabled; } }
            public bool IsDisabled { get { return !enabled; } }
            public bool IsOffline { get { return offline; } }
            public bool IsOnline { get { return !offline; } }
        }

        private ActivityHandler(AdjustConfig adjustConfig, DeviceUtil deviceUtil)
        {
            // default values
            _State.enabled = true;
            _State.offline = false;

            Init(adjustConfig, deviceUtil);
            _ActionQueue.Enqueue(InitI);
        }

        public void Init(AdjustConfig adjustConfig, DeviceUtil deviceUtil)
        {
            _Config = adjustConfig;
            _DeviceUtil = deviceUtil;
        }

        public static ActivityHandler GetInstance(AdjustConfig adjustConfig, DeviceUtil deviceUtil)
        {
            if (adjustConfig == null)
            {
                AdjustFactory.Logger.Error("AdjustConfig missing");
                return null;
            }

            if (!adjustConfig.IsValid())
            {
                AdjustFactory.Logger.Error("AdjustConfig not initialized correctly");
                return null;
            }

            ActivityHandler activityHandler = new ActivityHandler(adjustConfig, deviceUtil);
            return activityHandler;
        }

        public void TrackEvent(AdjustEvent adjustEvent)
        {
            _ActionQueue.Enqueue(() => TrackEventI(adjustEvent));
        }

        public void TrackSubsessionStart()
        {
            _ActionQueue.Enqueue(StartI);
        }

        public void TrackSubsessionEnd()
        {
            _ActionQueue.Enqueue(EndI);
        }

        public void FinishedTrackingActivity(ResponseData responseData)
        {
            // redirect session responses to attribution handler to check for attribution information
            if (responseData is SessionResponseData)
            {
                _AttributionHandler.CheckSessionResponse(responseData as SessionResponseData);
                return;
            }
        }

        public void SetEnabled(bool enabled)
        {
            if (!HasChangedState(
                previousState: IsEnabled(),
                newState: enabled,
                trueMessage: "Adjust already enabled",
                falseMessage: "Adjust already disabled"))
            {
                return;
            }

            _State.enabled = enabled;

            if (_ActivityState != null)
            {
                WriteActivityStateS(() => _ActivityState.Enabled = enabled);
            }

            UpdateStatusCondition(
                pausingState: !enabled,
                pausingMessage: "Pausing package and attribution handler to disable the SDK",
                remainsPausedMessage: "Package and attribution handler remain paused due to the SDK is offline",
                unPausingMessage: "Resuming package and attribution handler to enabled the SDK");
        }

        public bool IsEnabled()
        {
            if (_ActivityState != null)
            {
                return _ActivityState.Enabled;
            }
            else
            {
                return _State.enabled;
            }
        }

        public void SetOfflineMode(bool offline)
        {
            if (!HasChangedState(
                previousState: _State.IsOffline,
                newState: offline, 
                trueMessage: "Adjust already in offline mode",
                falseMessage: "Adjust already in online mode"))
            {
                return;
            }

            _State.offline = offline;

            UpdateStatusCondition(
                pausingState: offline,
                pausingMessage: "Pausing package and attribution handler to put in offline mode",
                remainsPausedMessage: "Package and attribution handler remain paused because the SDK is disabled",
                unPausingMessage: "Resuming package and attribution handler to put in online mode");
        }

        private bool HasChangedState(bool previousState, bool newState,
            string trueMessage, string falseMessage)
        {
            if (previousState != newState)
            {
                return true;
            }

            if (previousState)
            {
                _Logger.Debug(trueMessage);
            }
            else
            {
                _Logger.Debug(falseMessage);
            }

            return false;
        }

        private void UpdateStatusCondition(bool pausingState, string pausingMessage,
            string remainsPausedMessage, string unPausingMessage)
        {
            // it is changing from an active state to a pause state
            if (pausingState)
            {
                _Logger.Info(pausingMessage);
            }
            // check if it's remaining in a pause state
            else if (PausedI()) // safe to use internal version of paused (read only), can suffer from phantom read but not an issue
            {
                _Logger.Info(remainsPausedMessage);
            }
            else
            {
                // it is changing from a pause state to an active state
                _Logger.Info(unPausingMessage);
            }

            UpdateHandlersStatusAndSend();
        }

        public void OpenUrl(Uri uri)
        {
            _ActionQueue.Enqueue(() => OpenUrlI(uri));
        }

        public void LaunchSessionResponseTasks(SessionResponseData sessionResponseData)
        {
            _ActionQueue.Enqueue(() => LaunchSessionResponseTasksI(sessionResponseData));
        }

        public void LaunchAttributionResponseTasks(AttributionResponseData attributionResponseData)
        {
            _ActionQueue.Enqueue(() => LaunchAttributionResponseTasksI(attributionResponseData));
        }
     
        public void SetAskingAttribution(bool askingAttribution)
        {
            WriteActivityStateS(() => _ActivityState.AskingAttribution = askingAttribution);
        }

        public ActivityPackage GetAttributionPackage()
        {
            return GetAttributionPackageI();
        }

        public ActivityPackage GetDeeplinkClickPackage(Dictionary<string, string> extraParameters, 
            AdjustAttribution attribution, 
            string deeplink)
        {
            return GetDeeplinkClickPackageI(extraParameters, attribution, deeplink);
        }

        #region private
        private void WriteActivityState()
        {
            _ActionQueue.Enqueue(WriteActivityStateI);
        }

        private void WriteAttribution()
        {
            _ActionQueue.Enqueue(WriteAttributionI);
        }

        private void UpdateHandlersStatusAndSend()
        {
            _ActionQueue.Enqueue(UpdateHandlersStatusAndSendI);
        }

        private void InitI()
        {
            _DeviceInfo = _DeviceUtil.GetDeviceInfo();
            _DeviceInfo.SdkPrefix = _Config.SdkPrefix;

            ReadAttributionI();
            ReadActivityStateI();

            TimeSpan timerInterval = AdjustFactory.GetTimerInterval();
            TimeSpan timerStart = AdjustFactory.GetTimerStart();
            _SessionInterval = AdjustFactory.GetSessionInterval();
            _SubsessionInterval = AdjustFactory.GetSubsessionInterval();

            if (_Config.Environment.Equals(AdjustConfig.EnvironmentProduction))
            {
                _Logger.LogLevel = LogLevel.Assert;
            }
            
            if (_Config.EventBufferingEnabled)
            {
                _Logger.Info("Event buffering is enabled");
            }

            if (_Config.DefaultTracker != null)
            {
                _Logger.Info("Default tracker: '{0}'", _Config.DefaultTracker);
            }

            Util.ConfigureHttpClient(_DeviceInfo.ClientSdk);

            _PackageHandler = AdjustFactory.GetPackageHandler(this, PausedI());

            var attributionPackage = GetAttributionPackageI();

            _AttributionHandler = AdjustFactory.GetAttributionHandler(this,
                attributionPackage,
                PausedI(),
                _Config.HasAttributionDelegate);

            _SdkClickHandler = AdjustFactory.GetSdkClickHandler(PausedI());

            _Timer = new TimerCycle(_ActionQueue, TimerFiredI, timeInterval: timerInterval, timeStart: timerStart);

            StartI();
        }

        private void StartI()
        {
            // it shouldn't start if it was disabled after a first session
            if (_ActivityState != null
                && !_ActivityState.Enabled)
            {
                return;
            }

            UpdateHandlersStatusAndSendI();
            
            ProcessSessionI();

            CheckAttributionStateI();

            StartTimerI();
        }

        private void ProcessSessionI()
        {
            var now = DateTime.Now;

            // very firsts Session
            if (_ActivityState == null)
            {
                // create fresh activity state
                _ActivityState = new ActivityState();
                _ActivityState.SessionCount = 1; // first session

                TransferSessionPackageI();

                _ActivityState.ResetSessionAttributes(now);
                _ActivityState.Enabled = _State.IsEnabled;
                WriteActivityStateI();

                return;
            }

            var lastInterval = now - _ActivityState.LastActivity.Value;

            if (lastInterval.Ticks < 0)
            {
                _Logger.Error("Time Travel!");
                _ActivityState.LastActivity = now;
                WriteActivityStateI();
                return;
            }

            // new session
            if (lastInterval > _SessionInterval)
            {
                _ActivityState.SessionCount++;
                _ActivityState.LastInterval = lastInterval;

                TransferSessionPackageI();

                _ActivityState.ResetSessionAttributes(now);
                WriteActivityStateI();

                return;
            }

            // new subsession
            if (lastInterval > _SubsessionInterval)
            {
                _ActivityState.SubSessionCount++;
                _ActivityState.SessionLenght += lastInterval;
                _ActivityState.LastActivity = now;

                WriteActivityStateI();
                _Logger.Info("Started subsession {0} of session {1}",
                    _ActivityState.SubSessionCount, _ActivityState.SessionCount);
                return;
            }
        }

        private void CheckAttributionStateI()
        {
            // if it's a new session
            if (_ActivityState.SubSessionCount <= 1) { return; }

            // if there is already an attribution saved and there was no attribution being asked
            if (_Attribution != null && !_ActivityState.AskingAttribution) { return; }

            _AttributionHandler.GetAttribution();
        }

        private void EndI()
        {
            // pause sending if it's not allowed to send
            if (PausedI())
            {
                PauseSendingI();
            }

            if (UpdateActivityStateI(DateTime.Now))
            {
                WriteActivityStateI();
            }
        }

        private void TrackEventI(AdjustEvent adjustEvent)
        {
            if (!IsEnabledI()) { return; }
            if (!CheckEventI(adjustEvent)) { return; }

            var now = DateTime.Now;

            _ActivityState.EventCount++;
            UpdateActivityStateI(now);

            var packageBuilder = new PackageBuilder(_Config, _DeviceInfo, _ActivityState, now);
            ActivityPackage eventPackage = packageBuilder.BuildEventPackage(adjustEvent);
            _PackageHandler.AddPackage(eventPackage);

            if (_Config.EventBufferingEnabled)
            {
                _Logger.Info("Buffered event {0}", eventPackage.Suffix);
            }
            else
            {
                _PackageHandler.SendFirstPackage();
            }

            WriteActivityStateI();
        }

        private void LaunchSessionResponseTasksI(SessionResponseData sessionResponseData)
        {
            // try to update the attribution
            var attributionUpdated = UpdateAttributionI(sessionResponseData.Attribution);

            Task task = null;
            // if attribution changed, launch attribution changed delegate
            if (attributionUpdated)
            {
                task = LaunchAttributionActionI();
            }
        }

        private bool UpdateAttributionI(AdjustAttribution attribution)
        {
            if (attribution == null) { return false; }

            if (attribution.Equals(_Attribution)) { return false; }

            _Attribution = attribution;
            WriteAttributionI();

            return true;
        }

        private Task LaunchAttributionActionI()
        {
            if (_Config.AttributionChanged == null) { return null; }
            if (_Attribution == null) { return null; }

            return _DeviceUtil.RunActionInForeground(() => _Config.AttributionChanged(_Attribution));
        }

        private void LaunchAttributionResponseTasksI(AttributionResponseData attributionResponseData)
        {
            // try to update the attribution
            var attributionUpdated = UpdateAttributionI(attributionResponseData.Attribution);

            Task task = null;
            // if attribution changed, launch attribution changed delegate
            if (attributionUpdated)
            {
                task = LaunchAttributionActionI();
            }

            // if there is any, try to launch the deeplink
            LaunchDeepLink(attributionResponseData.Deeplink, task);
        }

        private void LaunchDeepLink(Uri deeplink, Task previousTask)
        {
            if (deeplink == null) { return; }
            _DeviceUtil.LauchDeeplink(deeplink, previousTask);
        }

        private void OpenUrlI(Uri uri)
        {
            if (uri == null) { return; }

            var deeplink = Uri.UnescapeDataString(uri.ToString());

            if (deeplink?.Length == 0) { return; }

            var windowsPhone80Protocol = "/Protocol?";
            if (deeplink?.StartsWith(windowsPhone80Protocol) == true)
            {
                deeplink = deeplink.Substring(windowsPhone80Protocol.Length);
            }

            var queryString = "";
            var queryStringIdx = deeplink.IndexOf("?");
            // check if '?' exists and it's not the last char
            if (queryStringIdx != -1 && queryStringIdx + 1 != deeplink.Length)
            {
                queryString = deeplink.Substring(queryStringIdx + 1);
            }

            // remove any possible fragments
            var fragmentIdx = queryString.LastIndexOf("#");
            if (fragmentIdx != -1)
            {
                queryString = queryString.Substring(0, fragmentIdx);
            }

            var queryPairs = queryString.Split('&');
            var extraParameters = new Dictionary<string, string>(queryPairs.Length);
            var attribution = new AdjustAttribution();

            foreach (var pair in queryPairs)
            {
                ReadQueryStringI(pair, extraParameters, attribution);
            }

            var clickPackage = GetDeeplinkClickPackageI(extraParameters, attribution, deeplink);

            _SdkClickHandler.SendSdkClick(clickPackage);
        }

        private ActivityPackage GetDeeplinkClickPackageI(Dictionary<string, string> extraParameters, 
            AdjustAttribution attribution,
            string deeplink)
        {
            var now = DateTime.Now;

            var clickBuilder = new PackageBuilder(_Config, _DeviceInfo, _ActivityState, now);
            clickBuilder.ExtraParameters = extraParameters;
            clickBuilder.Deeplink = deeplink;
            clickBuilder.Attribution = attribution;
            clickBuilder.ClickTime = now;

            var clickPackage = clickBuilder.BuildClickPackage("deeplink");

            return clickBuilder.BuildClickPackage("deeplink");
        }

        private void ReadQueryStringI(string queryString,
            Dictionary<string, string> extraParameters,
            AdjustAttribution attribution)
        {
            var pairComponents = queryString.Split('=');
            if (pairComponents.Length != 2) return;

            var key = pairComponents[0];
            if (!key.StartsWith(AdjustPrefix)) return;

            var value = pairComponents[1];
            if (value.Length == 0) return;

            var keyWOutPrefix = key.Substring(AdjustPrefix.Length);
            if (keyWOutPrefix.Length == 0) return;

            if (!ReadAttributionQueryStringI(attribution, keyWOutPrefix, value))
            {
                extraParameters.Add(keyWOutPrefix, value);
            }
        }

        private bool ReadAttributionQueryStringI(AdjustAttribution attribution,
            string key,
            string value)
        {
            if (key.Equals("tracker"))
            {
                attribution.TrackerName = value;
                return true;
            }

            if (key.Equals("campaign"))
            {
                attribution.Campaign = value;
                return true;
            }

            if (key.Equals("adgroup"))
            {
                attribution.Adgroup = value;
                return true;
            }

            if (key.Equals("creative"))
            {
                attribution.Creative = value;
                return true;
            }

            return false;
        }

        private bool IsEnabledI()
        {
            if (_ActivityState != null)
            {
                return _ActivityState.Enabled;
            }
            else
            {
                return _State.IsEnabled;
            }
        }

        private ActivityPackage GetAttributionPackageI()
        {
            var now = DateTime.Now;
            var packageBuilder = new PackageBuilder(_Config, _DeviceInfo, now);
            return packageBuilder.BuildAttributionPackage();
        }

        private void WriteActivityStateI()
        {
            WriteActivityStateS(null);
        }

        private void WriteActivityStateS(Action action)
        {
            lock (_ActivityStateLock)
            {
                action?.Invoke();

                Util.SerializeToFileAsync(
                    fileName: ActivityStateFileName,
                    objectWriter: ActivityState.SerializeToStream,
                    input: _ActivityState,
                    objectName: ActivityStateName)
                    .Wait();
            }
        }

        private void WriteAttributionI()
        {
            Util.SerializeToFileAsync(
                fileName: AttributionFileName, 
                objectWriter: AdjustAttribution.SerializeToStream,
                input: _Attribution,
                objectName: AttributionName)
                .Wait();
        }

        private void ReadActivityStateI()
        {
            _ActivityState = Util.DeserializeFromFileAsync(ActivityStateFileName,
                ActivityState.DeserializeFromStream, //deserialize function from Stream to ActivityState
                () => null, //default value in case of error
                ActivityStateName) // activity state name
                .Result;
        }

        private void ReadAttributionI()
        {
            _Attribution = Util.DeserializeFromFileAsync(AttributionFileName,
                AdjustAttribution.DeserializeFromStream, //deserialize function from Stream to Attribution
                () => null, //default value in case of error
                AttributionName) // attribution name
                .Result;
        }

        // return whether or not activity state should be written
        private bool UpdateActivityStateI(DateTime now)
        {
            var lastInterval = now - _ActivityState.LastActivity.Value;

            // ignore past updates
            if (lastInterval > _SessionInterval) { return false; }

            _ActivityState.LastActivity = now;

            if (lastInterval.Ticks < 0)
            {
                _Logger.Error("Time Travel!");
            }
            else
            {
                _ActivityState.SessionLenght += lastInterval;
                _ActivityState.TimeSpent += lastInterval;
            }

            return true;
        }

        private void TransferSessionPackageI()
        {
            // build Session Package
            var sessionBuilder = new PackageBuilder(_Config, _DeviceInfo, _ActivityState, DateTime.Now);
            var sessionPackage = sessionBuilder.BuildSessionPackage();

            // send Session Package
            _PackageHandler.AddPackage(sessionPackage);
            _PackageHandler.SendFirstPackage();
        }

        private void UpdateHandlersStatusAndSendI()
        {
            // check if it should stop sending
            if (PausedI())
            {
                PauseSendingI();
                return;
            }

            ResumeSendingI();

            // try to send
            if (!_Config.EventBufferingEnabled)
            {
                _PackageHandler.SendFirstPackage();
            }
        }

        private void PauseSendingI()
        {
            _AttributionHandler.PauseSending();
            _PackageHandler.PauseSending();
            _SdkClickHandler.PauseSending();
        }

        private void ResumeSendingI()
        {
            _AttributionHandler.ResumeSending();
            _PackageHandler.ResumeSending();
            _SdkClickHandler.ResumeSending();
        }

        private bool PausedI()
        {
            return _State.IsOffline || !IsEnabledI();
        }
        #endregion
        #region Timer

        private void StartTimerI()
        {
            if (PausedI())
            {
                return;
            }

            _Timer.Resume();
        }

        private void StopTimerI()
        {
            _Timer.Suspend();
        }

        private void TimerFiredI()
        {
            if (PausedI())
            {
                StopTimerI();
                return;
            }

            _Logger.Debug("Session timer fired");
            _PackageHandler.SendFirstPackage();

            if (UpdateActivityStateI(DateTime.Now))
            {
                WriteActivityStateI();
            }
        }

        #endregion Timer

        private bool CheckEventI(AdjustEvent adjustEvent)
        {
            if (adjustEvent == null)
            {
                _Logger.Error("Event missing");
                return false;
            }

            if (!adjustEvent.IsValid())
            {
                _Logger.Error("Event not initialized correctly");
                return false;
            }

            return true;
        }
    }
}