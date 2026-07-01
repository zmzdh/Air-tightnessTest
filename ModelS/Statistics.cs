using System;

namespace AudioActuatorCanTest.Models
{
    public class TestStatistics
    {
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double AvgTestDuration { get; set; }
    }

    public class DateStatistic
    {
        public DateTime Date { get; set; }
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double PassRate => TotalCount > 0 ? (double)PassCount / TotalCount : 0;
    }

    public class ModelStatistic
    {
        public string ProductModel { get; set; }
        public int TotalCount { get; set; }
        public int PassCount { get; set; }
        public int FailCount { get; set; }
        public double PassRate => TotalCount > 0 ? (double)PassCount / TotalCount : 0;
    }

    public class FailReasonStatistic
    {
        public string FailReason { get; set; }
        public int Count { get; set; }
        public double Percentage { get; set; }
    }

    public class UserStatistics
    {
        public int LoginCount { get; set; }
        public int TestCount { get; set; }
    }
}
