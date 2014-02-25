﻿using System;
namespace AdjustSdk.Pcl
{
    interface IPackageHandler
    {
        void AddPackage(ActivityPackage activityPackage);
        void CloseFirstPackage();
        void FinishedTrackingActivity(ActivityPackage activityPackage, AdjustSdk.ResponseData responseData);
        void PauseSending();
        void ResumeSending();
        void SendFirstPackage();
        void SendNextPackage();
    }
}
