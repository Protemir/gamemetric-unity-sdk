using System.Collections.Generic;
using GameMetricSDK;
using UnityEngine;

namespace GameMetricSDK.Samples
{
    /// <summary>
    /// Drop-in demo: attach this to any GameObject in an empty scene and press
    /// Play. It renders four buttons via OnGUI and mirrors the SDK's own
    /// "[GameMetric]" console logs onto the screen, so you can watch events get
    /// queued, delivered, cached, or retried in real time.
    ///
    /// Leave "API Key" blank to initialize from Edit → Project Settings →
    /// GameMetric, or type a key to initialize inline (debug logs are forced on
    /// in that case so delivery is visible).
    /// </summary>
    public sealed class GameMetricDemoUI : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Optional. Blank uses the key from Project Settings → GameMetric.")]
        private string apiKey = "";

        private const int MaxLines = 200;

        private readonly List<string> _log = new List<string>();
        private Vector2 _scroll;
        private int _eventCount;

        private static GUIStyle _title;
        private static GUIStyle _button;

        private void OnEnable()
        {
            Application.logMessageReceived += HandleUnityLog;
        }

        private void OnDisable()
        {
            Application.logMessageReceived -= HandleUnityLog;
        }

        // Mirror the SDK's own logs onto the on-screen console.
        private void HandleUnityLog(string condition, string stackTrace, LogType type)
        {
            if (!string.IsNullOrEmpty(condition) && condition.Contains("[GameMetric]"))
            {
                Append(condition);
            }
        }

        private void Append(string line)
        {
            _log.Add(line);
            if (_log.Count > MaxLines)
            {
                _log.RemoveAt(0);
            }

            _scroll.y = float.MaxValue; // keep pinned to the newest line
        }

        private void OnGUI()
        {
            EnsureStyles();

            const float pad = 12f;
            var width = Mathf.Min(480f, Screen.width - pad * 2f);
            GUILayout.BeginArea(new Rect(pad, pad, width, Screen.height - pad * 2f), GUI.skin.box);

            GUILayout.Label("GameMetric SDK — Demo", _title);
            GUILayout.Label(GameMetric.IsInitialized
                ? "Status: Initialized (session " + ShortId(GameMetric.SessionId) + ")"
                : "Status: Not initialized");

            GUILayout.Space(4f);
            GUILayout.Label("API Key (blank = use Project Settings):");
            apiKey = GUILayout.TextField(apiKey);

            GUILayout.Space(6f);
            if (GUILayout.Button("Initialize SDK", _button))
            {
                DoInitialize();
            }

            GUI.enabled = GameMetric.IsInitialized;
            if (GUILayout.Button("Log Test Event", _button))
            {
                DoLogEvent();
            }

            if (GUILayout.Button("Log Monetization ($4.99)", _button))
            {
                DoLogMonetization();
            }

            if (GUILayout.Button("Force Flush", _button))
            {
                DoFlush();
            }

            GUI.enabled = true;

            GUILayout.Space(8f);
            GUILayout.Label("Log:");
            _scroll = GUILayout.BeginScrollView(_scroll, GUI.skin.box, GUILayout.ExpandHeight(true));
            for (var i = 0; i < _log.Count; i++)
            {
                GUILayout.Label(_log[i]);
            }

            GUILayout.EndScrollView();

            GUILayout.EndArea();
        }

        private void DoInitialize()
        {
            if (GameMetric.IsInitialized)
            {
                Append("Already initialized.");
                return;
            }

            if (string.IsNullOrEmpty(apiKey))
            {
                Append("Initializing from Project Settings…");
                GameMetric.Initialize();
            }
            else
            {
                Append("Initializing with inline API key…");
                // Force debug logs on so the demo shows delivery activity.
                GameMetric.Initialize(apiKey, "1.0.0-demo", true);
            }
        }

        private void DoLogEvent()
        {
            _eventCount++;
            GameMetric.LogEvent("demo_button_click", new Dictionary<string, object>
            {
                { "source", "demo_ui" },
                { "count", _eventCount },
            });
            Append("Queued demo_button_click #" + _eventCount);
        }

        private void DoLogMonetization()
        {
            GameMetric.LogMonetization("demo.gems_pack", 4.99, "USD");
            Append("Queued in_app_purchase $4.99");
        }

        private void DoFlush()
        {
            GameMetric.Flush();
            Append("Flush requested.");
        }

        private static string ShortId(string id)
        {
            return string.IsNullOrEmpty(id) ? "-" : id.Substring(0, Mathf.Min(8, id.Length));
        }

        private static void EnsureStyles()
        {
            if (_title == null)
            {
                _title = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            }

            if (_button == null)
            {
                _button = new GUIStyle(GUI.skin.button) { fontSize = 13, fixedHeight = 30f };
            }
        }
    }
}