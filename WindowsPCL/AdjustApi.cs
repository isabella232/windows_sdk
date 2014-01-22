﻿using System.Collections.Generic;

namespace adeven.AdjustIo.PCL
{
    public class AdjustApi
    {
        // keep this consts in sync with WS and WP AdjustIo class
        private static string EnvironmentSandbox = "sandbox";

        private static string EnvironmentProduction = "production";

        private static ActivityHandler activityHandler;
        private static DeviceUtil DeviceSpecific;

        public static void AppDidLaunch(string appToken, DeviceUtil deviceSpecific)
        {
            DeviceSpecific = deviceSpecific;
            activityHandler = new ActivityHandler(appToken, deviceSpecific);
        }

        public static void AppDidActivate()
        {
            activityHandler.TrackSubsessionStart();
        }

        public static void AppDidDeactivate()
        {
            activityHandler.TrackSubsessionEnd();
        }

        public static void TrackEvent(string eventToken,
            Dictionary<string, string> callbackParameters = null)
        {
            activityHandler.TrackEvent(eventToken, callbackParameters);
        }

        public static void TrackRevenue(double amountInCents,
            string eventToken = null,
            Dictionary<string, string> callbackParameters = null)
        {
            activityHandler.TrackRevenue(amountInCents, eventToken, callbackParameters);
        }

        public static void SetLogLevel(LogLevel logLevel)
        {
            PCL.Logger.LogLevel = (PCL.LogLevel)logLevel;
        }

        public static void SetEnvironment(string environment)
        {
            if (activityHandler == null)
            {
                Logger.Error("Please call 'SetEnvironment' after 'AppDidLaunch'!");
            }
            else if (environment == EnvironmentSandbox)
            {
                activityHandler.SetEnvironment(environment);
                Logger.Assert("SANDBOX: AdjustIo is running in Sandbox mode. Use this setting for testing."
                    + " Don't forget to set the environment to AIEnvironmentProduction before publishing!");
            }
            else if (environment == EnvironmentProduction)
            {
                activityHandler.SetEnvironment(environment);
                Logger.Assert("PRODUCTION: AdjustIo is running in Production mode."
                    + " Use this setting only for the build that you want to publish."
                    + " Set the environment to AIEnvironmentSandbox if you want to test your app!");
            }
            else
            {
                activityHandler.SetEnvironment("malformed");
                Logger.Error("Malformerd environment: '{0}'", environment);
            }
        }

        public static void SetEventBufferingEnabled(bool enabledEventBuffering)
        {
            if (activityHandler == null)
            {
                Logger.Error("Please call 'SetEventBufferingEnabled' after 'AppDidLaunch'!");
                return;
            }

            activityHandler.SetBufferedEvents(enabledEventBuffering);

            if (ActivityHandler.IsBufferedEventsEnabled)
                Logger.Info("Event buffering is enabled");
        }
    }
}