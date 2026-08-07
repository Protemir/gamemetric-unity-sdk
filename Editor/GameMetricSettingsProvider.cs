using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace GameMetricSDK.EditorTools
{
    /// <summary>
    /// Registers Edit → Project Settings → GameMetric and edits the
    /// <see cref="GameMetricSettings"/> asset, creating it at
    /// Assets/Resources/GameMetricSettings.asset on first use so the runtime can
    /// load it via Resources. Also exposes a Tools menu shortcut and an editor-time
    /// "send test event" button for verifying the API key end-to-end.
    /// </summary>
    public static class GameMetricSettingsProvider
    {
        private const string SettingsPath = "Project/GameMetric";
        private const string AssetPath = "Assets/Resources/GameMetricSettings.asset";
        private const string ResourcesDir = "Assets/Resources";

        private static string _testStatus = "";
        private static bool _testInProgress;

        [MenuItem("Tools/GameMetric/Open Settings")]
        public static void OpenSettings()
        {
            SettingsService.OpenProjectSettings(SettingsPath);
        }

        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SettingsPath, SettingsScope.Project)
            {
                label = "GameMetric",
                keywords = new HashSet<string>(new[] { "GameMetric", "analytics", "api", "apiKey", "version", "telemetry" }),
                guiHandler = _ =>
                {
                    var settings = GetOrCreateSettings();
                    var serialized = new SerializedObject(settings);

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Ingestion", EditorStyles.boldLabel);

                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(serialized.FindProperty("apiKey"), new GUIContent("Api Key"));
                    EditorGUILayout.PropertyField(serialized.FindProperty("gameVersion"), new GUIContent("Game Version"));
                    EditorGUILayout.PropertyField(serialized.FindProperty("enableDebugLogs"), new GUIContent("Enable Debug Logs"));
                    EditorGUILayout.PropertyField(
                        serialized.FindProperty("baseUrl"),
                        new GUIContent("Base URL (optional)", "Leave blank for production. Override for local/staging, e.g. http://localhost:5000."));

                    if (EditorGUI.EndChangeCheck())
                    {
                        serialized.ApplyModifiedProperties();
                        EditorUtility.SetDirty(settings);
                        AssetDatabase.SaveAssets();
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);

                    var hasKey = !string.IsNullOrEmpty(settings.ApiKey);
                    using (new EditorGUI.DisabledScope(_testInProgress || !hasKey))
                    {
                        if (GUILayout.Button(_testInProgress ? "Sending…" : "Send Test Event to Server"))
                        {
                            SendTestEvent(settings.ApiKey, settings.GameVersion, settings.BaseUrl);
                        }
                    }

                    if (!hasKey)
                    {
                        EditorGUILayout.HelpBox("Enter an API Key above to enable the test.", MessageType.None);
                    }

                    if (!string.IsNullOrEmpty(_testStatus))
                    {
                        EditorGUILayout.HelpBox(
                            _testStatus,
                            _testStatus.StartsWith("Success", StringComparison.Ordinal) ? MessageType.Info : MessageType.Error);
                    }

                    EditorGUILayout.Space();
                    EditorGUILayout.HelpBox(
                        "Call GameMetric.Initialize() with no arguments to use these settings at runtime. " +
                        "Blank Game Version falls back to Application.version.",
                        MessageType.Info);
                },
            };
        }

        /// <summary>
        /// Sends a single test event to the ingestion endpoint from the editor
        /// (no Play Mode required). UnityWebRequest works in edit mode; the
        /// completed callback fires via the editor update loop.
        /// </summary>
        private static void SendTestEvent(string apiKey, string gameVersion, string baseUrl)
        {
            var version = string.IsNullOrEmpty(gameVersion) ? "editor" : gameVersion;
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

            // Honor the configured base URL so the test button works against a local
            // backend too; blank falls back to the production ingestion endpoint.
            var endpoint = string.IsNullOrWhiteSpace(baseUrl)
                ? GameMetricConfig.DefaultEndpointUrl
                : baseUrl.TrimEnd('/') + "/v1/events";

            var payload =
                "[{\"user_id\":\"editor-test\",\"session_id\":\"editor-session\",\"event_name\":\"editor_test_event\"," +
                "\"event_time\":\"" + timestamp + "\",\"platform\":\"editor\",\"version\":\"" + Escape(version) + "\"," +
                "\"properties\":{\"source\":\"project_settings_test_button\"}}]";

            var request = new UnityWebRequest(endpoint, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(payload)),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("X-API-Key", apiKey);

            _testInProgress = true;
            _testStatus = "";

            var operation = request.SendWebRequest();
            operation.completed += _ =>
            {
                var success = request.result == UnityWebRequest.Result.Success
                    && request.responseCode >= 200 && request.responseCode < 300;

                if (success)
                {
                    _testStatus = "Success (" + request.responseCode + "): " + request.downloadHandler.text;
                }
                else
                {
                    var detail = string.IsNullOrEmpty(request.error) ? ("HTTP " + request.responseCode) : request.error;
                    _testStatus = "Error (" + request.responseCode + "): " + detail;
                }

                request.Dispose();
                _testInProgress = false;

                EditorUtility.DisplayDialog("GameMetric — Test Event", _testStatus, "OK");
            };
        }

        private static string Escape(string value)
        {
            return value == null ? "" : value.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static GameMetricSettings GetOrCreateSettings()
        {
            var settings = AssetDatabase.LoadAssetAtPath<GameMetricSettings>(AssetPath);
            if (settings != null)
            {
                return settings;
            }

            // It may already live elsewhere under a Resources folder.
            settings = Resources.Load<GameMetricSettings>(GameMetricSettings.ResourceName);
            if (settings != null)
            {
                return settings;
            }

            if (!Directory.Exists(ResourcesDir))
            {
                Directory.CreateDirectory(ResourcesDir);
                AssetDatabase.Refresh();
            }

            settings = ScriptableObject.CreateInstance<GameMetricSettings>();
            AssetDatabase.CreateAsset(settings, AssetPath);
            AssetDatabase.SaveAssets();
            return settings;
        }
    }
}