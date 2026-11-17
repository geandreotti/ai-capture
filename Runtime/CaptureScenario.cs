using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChromeHypercube.AICapture
{
    public enum CaptureStepType
    {
        /// <summary>Waits floatValue seconds (real time, ignores timeScale).</summary>
        Wait,
        /// <summary>Waits intValue frames.</summary>
        WaitFrames,
        /// <summary>Waits until the [CaptureCondition] named targetName returns true. Timeout = floatValue (default 10s).</summary>
        WaitUntil,
        /// <summary>Saves a screenshot named targetName_{locale}.png.</summary>
        Capture,
        /// <summary>Switches the locale to targetName and calls the "set_locale" action.</summary>
        SetLocale,
        /// <summary>Sets the Game View resolution to intValue x extraIntValue.</summary>
        SetResolution,
        /// <summary>Sets Time.timeScale = floatValue.</summary>
        SetTimeScale,
        /// <summary>Calls the [CaptureAction] named targetName, passing args.</summary>
        InvokeAction,
        /// <summary>Waits until the screen stops changing (timeout = floatValue, default 5s; a timeout only logs a warning).</summary>
        WaitForStable
    }

    [Serializable]
    public class CaptureStep
    {
        public CaptureStepType stepType;
        [Tooltip("Capture: file name. WaitUntil: condition id. InvokeAction: action id. SetLocale: locale code.")]
        public string targetName;
        [Tooltip("Wait: seconds. WaitUntil: timeout in seconds. SetTimeScale: time scale.")]
        public float floatValue;
        [Tooltip("WaitFrames: frame count. SetResolution: width.")]
        public int intValue;
        [Tooltip("SetResolution: height.")]
        public int extraIntValue;
        [Tooltip("Arguments passed to actions and conditions that take string[].")]
        public string[] args;
    }

    /// <summary>Overrides the scenario's comparison settings for one capture (matched by the Capture step's targetName).</summary>
    [Serializable]
    public class CaptureRule
    {
        [Tooltip("The Capture step's targetName, e.g. explore. Also matches explore_pt, explore_en...")]
        public string captureName;
        [Tooltip("-1 = use the scenario value.")]
        public float colorTolerance = -1f;
        [Tooltip("-1 = use the scenario value.")]
        public float maxMismatchPercent = -1f;
        [Tooltip("-1 = use the scenario value; 0 = off.")]
        public int maxRegionPixels = -1;
        [Tooltip("Pixel rectangles left out of the comparison (live content). Origin top-left, like the report's boundingBoxes.")]
        public List<RectInt> ignoreRegions = new List<RectInt>();
    }

    [CreateAssetMenu(fileName = "NewCaptureScenario", menuName = "AI Capture/Capture Scenario")]
    public class CaptureScenario : ScriptableObject
    {
        [Tooltip("Used for the output and golden image folder names.")]
        public string scenarioId = "home";
        public string defaultLocale = "en";
        public int targetWidth = 1080;
        public int targetHeight = 2400;
        [Tooltip("Per-channel difference (0-1) a pixel may have before counting as changed. Raise it for scenes with live content.")]
        [Range(0f, 1f)] public float colorTolerance = 0.05f;
        [Tooltip("Fraction of changed pixels (0.01 = 1%) above which a capture fails.")]
        [Range(0f, 1f)] public float maxMismatchPercent = 0.01f;
        [Tooltip("Fails a capture when any single changed region is larger than this many pixels (box width x height). 0 = off. Catches a small broken element that the percentage hides.")]
        [Min(0)] public int maxRegionPixels = 0;
        [Tooltip("A capture with no golden fails instead of becoming the golden. Approve it with Accept Last Run as Golden.")]
        public bool strictGoldens = true;
        [Tooltip("Per-capture overrides and ignored regions.")]
        public List<CaptureRule> rules = new List<CaptureRule>();
        public List<CaptureStep> steps = new List<CaptureStep>();
    }
}
