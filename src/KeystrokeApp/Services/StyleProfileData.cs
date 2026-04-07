using System;
using System.Collections.Generic;

namespace KeystrokeApp.Services;

public class StyleProfileData
{
    public DateTime LastUpdated { get; set; }
    public int EntriesProcessed { get; set; }
    public string GeneralProfile { get; set; } = "";
    public Dictionary<string, string> CategoryProfiles { get; set; } = new();
    public Dictionary<string, string> ContextProfiles { get; set; } = new();
    public Dictionary<string, string> ContextLabels { get; set; } = new();

    /// <summary>
    /// Rolling quality snapshots pushed after each profile generation (Sub-Phase D).
    /// Keeps the last 3 so LearningScoreService can detect quality drift across
    /// multiple profile refresh cycles. Oldest is dropped when a 4th is added.
    /// </summary>
    public List<QualitySnapshot> QualitySnapshots { get; set; } = new();

    public class QualitySnapshot
    {
        public DateTime Timestamp    { get; set; }
        /// <summary>Average quality score (0–1) of accepted completions at this point in time.</summary>
        public float    AvgQuality   { get; set; }
        public int      SampleCount  { get; set; }
    }
}
