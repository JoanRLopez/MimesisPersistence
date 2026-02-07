#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using MelonLoader;
using Mimic.Voice.SpeechSystem;
using MimesisPersistence.Patches;
using ReluReplay.Serializer;
using UnityEngine;

namespace MimesisPersistence
{
    /// <summary>
    /// DEBUG-only tool to browse, listen, save and load SpeechEvents.
    /// Driven by MelonMod.OnUpdate() and MelonMod.OnGUI() callbacks.
    /// Press F9 to toggle.
    /// </summary>
    public class DebugAudioTester
    {
        private readonly MelonLogger.Instance _log;
        private bool _showWindow = false;
        private Rect _windowRect = new Rect(20, 20, 520, 750);

        // Live events from game archives
        private List<SpeechEvent> _liveEvents = new List<SpeechEvent>();
        // Loaded events from debug file
        private List<SpeechEvent> _loadedEvents = new List<SpeechEvent>();

        private int _selectedLiveIdx = -1;
        private int _selectedLoadedIdx = -1;
        private Vector2 _scrollLive = Vector2.zero;
        private Vector2 _scrollLoaded = Vector2.zero;

        // Playback
        private GameObject _audioObj;
        private AudioSource _audioSource;

        // Status
        private string _status = "Ready. Press F9.";

        private const string DEBUG_FILE = "debug_speech_events.bin";

        // Reflection caches
        private static readonly FieldInfo AudioDataField =
            typeof(SpeechEvent).GetField("CompressedAudioData", BindingFlags.Public | BindingFlags.Instance);

        // Opus decode cache
        private static Type _opusType;
        private static MethodInfo _isOpusMethod;
        private static MethodInfo _toClipMethod;
        private static bool _opusResolved = false;

        // New Input System reflection
        private static Type _keyboardType;
        private static Type _keyType;
        private static PropertyInfo _keyboardCurrentProp;
        private static PropertyInfo _keyboardIndexer;
        private static object _f9KeyValue;
        private static bool _inputSystemReady = false;
        private static bool _inputSystemFailed = false;

        public DebugAudioTester(MelonLogger.Instance logger)
        {
            _log = logger;
        }

        private void EnsureAudioSource()
        {
            if (_audioSource != null) return;
            _audioObj = new GameObject("MimesisPersistence_DebugAudio");
            UnityEngine.Object.DontDestroyOnLoad(_audioObj);
            _audioSource = _audioObj.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
        }

        public void HandleInput()
        {
            try
            {
                if (WasKeyPressedThisFrame())
                {
                    _showWindow = !_showWindow;
                    _log.Msg($"[DEBUG] Audio Debug window: {(_showWindow ? "OPEN" : "CLOSED")}");
                }
            }
            catch { }
        }

        public void DrawGUI()
        {
            if (!_showWindow) return;
            _windowRect = GUI.Window(99887, _windowRect, DrawWindow, "MimesisPersistence Debug (F9)");
        }

        public void Cleanup()
        {
            if (_audioObj != null)
                UnityEngine.Object.Destroy(_audioObj);
        }

        // ===================== WINDOW =====================

        private void DrawWindow(int id)
        {
            GUILayout.Label(_status);
            GUILayout.Space(5);

            // === LIVE EVENTS (from game memory) ===
            GUILayout.Label($"=== LIVE SpeechEvents ({_liveEvents.Count}) ===");

            if (GUILayout.Button("Refresh from game", GUILayout.Height(25)))
                RefreshLiveEvents();

            if (_liveEvents.Count > 0)
            {
                _scrollLive = GUILayout.BeginScrollView(_scrollLive, GUILayout.Height(150));
                for (int i = 0; i < _liveEvents.Count; i++)
                {
                    var ev = _liveEvents[i];
                    int audioSz = ev.CompressedAudioData?.Length ?? 0;
                    string label = $"#{i} [{ev.PlayerName}] {ev.Duration:F1}s audio={audioSz}B";

                    bool selected = (i == _selectedLiveIdx);
                    if (GUILayout.Toggle(selected, label) != selected)
                        _selectedLiveIdx = selected ? -1 : i;
                }
                GUILayout.EndScrollView();

                GUILayout.BeginHorizontal();
                GUI.enabled = _selectedLiveIdx >= 0 && _selectedLiveIdx < _liveEvents.Count;
                if (GUILayout.Button("Play selected", GUILayout.Height(25)))
                    PlaySpeechEvent(_liveEvents[_selectedLiveIdx], "live");
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                // Detail panel for selected live event
                if (_selectedLiveIdx >= 0 && _selectedLiveIdx < _liveEvents.Count)
                    DrawEventDetails(_liveEvents[_selectedLiveIdx]);
            }

            GUILayout.Space(5);

            // === SAVE / LOAD ===
            GUILayout.Label("=== SAVE / LOAD ===");
            GUILayout.BeginHorizontal();
            GUI.enabled = _liveEvents.Count > 0;
            if (GUILayout.Button("Save ALL live events", GUILayout.Height(28)))
                SaveAllEvents(_liveEvents);
            GUI.enabled = true;
            if (GUILayout.Button("Load from file", GUILayout.Height(28)))
                LoadEventsFromFile();
            GUILayout.EndHorizontal();

            GUILayout.Space(5);

            // === EVENT POOL STATUS ===
            if (SpeechEventPoolManager.IsLoaded)
            {
                var (pending, injected, fallback) = SpeechEventPoolManager.GetCounts();
                GUILayout.Label($"=== Event Pool ({SpeechEventPoolManager.TotalCount} total) ===");
                GUILayout.Label($"  PENDING={pending}  INJECTED={injected}  FALLBACK={fallback}");

                if (pending > 0)
                {
                    if (GUILayout.Button($"Force FALLBACK ({pending} pending -> local)", GUILayout.Height(28)))
                        DoForceFallback();
                }
            }

            // === DISCONNECTED CACHE STATUS ===
            int dcCount = SpeechEventPoolManager.DisconnectedCacheCount;
            if (dcCount > 0)
            {
                GUILayout.Space(3);
                var oldBg = GUI.backgroundColor;
                GUI.backgroundColor = new Color(1f, 0.85f, 0.6f);
                GUILayout.BeginVertical("box");
                GUI.backgroundColor = oldBg;

                GUILayout.Label($"=== Disconnected Cache ({dcCount} events) ===");

                // Show per-player breakdown
                var dcEvents = SpeechEventPoolManager.GetDisconnectedEvents();
                var byPlayer = new Dictionary<string, int>();
                foreach (var ev in dcEvents)
                {
                    string name = ev.PlayerName ?? "(unknown)";
                    if (!byPlayer.ContainsKey(name)) byPlayer[name] = 0;
                    byPlayer[name]++;
                }
                foreach (var kvp in byPlayer)
                    GUILayout.Label($"  [{kvp.Key}] = {kvp.Value} events");

                // Show cached player mappings
                var dcMappings = SpeechEventPoolManager.GetDisconnectedPlayerMappings();
                if (dcMappings.Count > 0)
                {
                    GUILayout.Label($"  Cached mappings: {dcMappings.Count}");
                    foreach (var kvp in dcMappings)
                        GUILayout.Label($"    SteamID {kvp.Key} -> '{kvp.Value}'");
                }

                GUILayout.Label("<color=#aaaaaa>Will be saved on next save or restored on reconnect.</color>",
                    _richStyle ?? GUIStyle.none);

                GUILayout.EndVertical();
            }

            GUILayout.Space(5);

            // === LOADED EVENTS (from file) ===
            if (_loadedEvents.Count > 0)
            {
                GUILayout.Label($"=== LOADED SpeechEvents ({_loadedEvents.Count}) ===");

                _scrollLoaded = GUILayout.BeginScrollView(_scrollLoaded, GUILayout.Height(150));
                for (int i = 0; i < _loadedEvents.Count; i++)
                {
                    var ev = _loadedEvents[i];
                    int audioSz = ev.CompressedAudioData?.Length ?? 0;
                    string audioStatus = audioSz > 0 ? $"{audioSz}B" : "NO AUDIO!";
                    string label = $"#{i} [{ev.PlayerName}] {ev.Duration:F1}s audio={audioStatus}";

                    bool selected = (i == _selectedLoadedIdx);
                    if (GUILayout.Toggle(selected, label) != selected)
                        _selectedLoadedIdx = selected ? -1 : i;
                }
                GUILayout.EndScrollView();

                GUILayout.BeginHorizontal();
                GUI.enabled = _selectedLoadedIdx >= 0 && _selectedLoadedIdx < _loadedEvents.Count;
                if (GUILayout.Button("Play selected", GUILayout.Height(25)))
                    PlaySpeechEvent(_loadedEvents[_selectedLoadedIdx], "loaded");
                GUI.enabled = true;
                GUILayout.EndHorizontal();

                // Detail panel for selected loaded event
                if (_selectedLoadedIdx >= 0 && _selectedLoadedIdx < _loadedEvents.Count)
                    DrawEventDetails(_loadedEvents[_selectedLoadedIdx]);
            }

            GUI.DragWindow();
        }

        // ===================== EVENT DETAILS =====================

        private void DrawEventDetails(SpeechEvent ev)
        {
            GUILayout.Space(3);
            var oldColor = GUI.color;
            GUI.color = new Color(0.85f, 0.95f, 1f);
            GUILayout.BeginVertical("box");
            GUI.color = oldColor;

            GUILayout.Label("<b>--- Selected Event Details ---</b>", _richStyle ?? GUIStyle.none);

            // Basic info
            int audioBytes = ev.CompressedAudioData?.Length ?? 0;
            string audioInfo = audioBytes > 0
                ? $"{audioBytes:N0} bytes ({audioBytes / 1024f:F1} KB)"
                : "<color=red>NO AUDIO DATA</color>";

            DrawDetailRow("ID", ev.Id.ToString());
            DrawDetailRow("Player", ev.PlayerName ?? "(null)");
            DrawDetailRow("Duration", $"{ev.Duration:F2}s");
            DrawDetailRow("Audio", audioInfo);
            DrawDetailRow("Channels", ev.Channels.ToString());
            DrawDetailRow("Sample Rate", $"{ev.SampleRate} Hz");
            DrawDetailRow("Orig Audio Len", ev.OriginalAudioDataLength.ToString("N0"));
            DrawDetailRow("Avg Amplitude", $"{ev.AverageAmplitude:F4}");
            DrawDetailRow("RecordedTime", $"{ev.RecordedTime:F2}s");
            DrawDetailRow("LastPlayedTime", $"{ev.LastPlayedTime:F2}s");
            DrawDetailRow("PlayedCount", ev.AudioPlayedCount.ToString());

            // GameData
            if (ev.GameData != null)
            {
                GUILayout.Space(3);
                GUILayout.Label("<b>GameData:</b>", _richStyle ?? GUIStyle.none);
                DrawDetailRow("  Area", ev.GameData.Area.ToString());
                DrawDetailRow("  GameTime", ev.GameData.GameTime.ToString());
                DrawDetailRow("  Adjacent Players", ev.GameData.AdjacentPlayerCount.ToString());
                DrawDetailRow("  Facing Players", ev.GameData.FacingPlayerCount.ToString());
                DrawDetailRow("  Teleporter", ev.GameData.Teleporter.ToString());
                DrawDetailRow("  IndoorEntered", ev.GameData.IndoorEntered.ToString());
                DrawDetailRow("  Charger", ev.GameData.Charger.ToString());
                DrawDetailRow("  CrowShop", ev.GameData.CrowShop.ToString());

                if (ev.GameData.ScrapObjects != null && ev.GameData.ScrapObjects.Count > 0)
                    DrawDetailRow("  Scrap", string.Join(", ", ev.GameData.ScrapObjects));
                if (ev.GameData.Monsters != null && ev.GameData.Monsters.Count > 0)
                    DrawDetailRow("  Monsters", string.Join(", ", ev.GameData.Monsters));
                if (ev.GameData.IncomingEvent != null && ev.GameData.IncomingEvent.Count > 0)
                {
                    var incomingStr = string.Join(", ", ev.GameData.IncomingEvent.Select(
                        ie => ie.EventType.ToString()));
                    DrawDetailRow("  IncomingEvents", incomingStr);
                }
            }
            else
            {
                GUILayout.Label("<color=yellow>GameData: null</color>", _richStyle ?? GUIStyle.none);
            }

            GUILayout.EndVertical();
        }

        private static GUIStyle _richStyle;
        private static GUIStyle _detailRowStyle;

        private void EnsureStyles()
        {
            if (_richStyle == null)
            {
                _richStyle = new GUIStyle(GUI.skin.label) { richText = true };
            }
            if (_detailRowStyle == null)
            {
                _detailRowStyle = new GUIStyle(GUI.skin.label)
                {
                    richText = true,
                    fontSize = 11,
                    padding = new RectOffset(4, 4, 1, 1)
                };
            }
        }

        private void DrawDetailRow(string label, string value)
        {
            EnsureStyles();
            GUILayout.Label($"<color=#aaaaaa>{label}:</color> {value}", _detailRowStyle);
        }

        // ===================== REFRESH =====================

        private void RefreshLiveEvents()
        {
            try
            {
                _liveEvents = MimesisSaveManager.CollectAllSpeechEvents();
                _selectedLiveIdx = -1;

                int withAudio = 0;
                int withoutAudio = 0;
                foreach (var ev in _liveEvents)
                {
                    if (ev.CompressedAudioData != null && ev.CompressedAudioData.Length > 0)
                        withAudio++;
                    else
                        withoutAudio++;
                }

                _status = $"Found {_liveEvents.Count} events ({withAudio} with audio, {withoutAudio} without)";
                _log.Msg($"[DEBUG] Refreshed: {_liveEvents.Count} events ({withAudio} with audio, {withoutAudio} without)");
            }
            catch (Exception ex)
            {
                _status = $"Refresh error: {ex.Message}";
            }
        }

        // ===================== FALLBACK =====================

        private void DoForceFallback()
        {
            try
            {
                var localArchive = SpeechEventPoolManager.GetLocalArchive();
                if (localArchive == null)
                {
                    _status = "FALLBACK failed: no local archive reference!";
                    return;
                }

                var events = SpeechEventPoolManager.ForceFallbackToLocal();
                if (events.Count == 0)
                {
                    _status = "No pending events to fallback.";
                    return;
                }

                int added = SpeechEventArchivePatches.InjectEventsIntoArchive(localArchive, events);
                var counts = SpeechEventPoolManager.GetCounts();
                _status = $"FALLBACK: {added} events -> local archive " +
                          $"[{counts.pending}P/{counts.injected}I/{counts.fallback}F]";
                _log.Msg($"[DEBUG] {_status}");
            }
            catch (Exception ex)
            {
                _status = $"FALLBACK error: {ex.Message}";
                _log.Warning($"[DEBUG] FALLBACK error: {ex}");
            }
        }

        // ===================== PLAYBACK =====================

        private void PlaySpeechEvent(SpeechEvent ev, string source)
        {
            if (ev == null) { _status = "No event selected."; return; }

            byte[] audio = ev.CompressedAudioData;
            if (audio == null || audio.Length == 0)
            {
                _status = $"FAIL: {source} event has NO audio! CompressedAudioData is null/empty.";
                return;
            }

            try
            {
                EnsureAudioSource();
                ResolveOpus();

                AudioClip clip = null;
                string method = "?";

                // Try Opus decode (real game events)
                if (_toClipMethod != null && _isOpusMethod != null)
                {
                    bool isOpus = (bool)_isOpusMethod.Invoke(null, new object[] { audio });
                    if (isOpus)
                    {
                        clip = _toClipMethod.Invoke(null, new object[] { audio, ev.Id.ToString() }) as AudioClip;
                        method = "Opus";
                    }
                }

                if (clip != null)
                {
                    _audioSource.Stop();
                    _audioSource.PlayOneShot(clip);
                    _status = $"Playing {source}! [{ev.PlayerName}] {clip.length:F2}s ({method}, {audio.Length}B)";
                }
                else
                {
                    _status = $"Cannot decode audio. Opus={_toClipMethod != null}, IsOpus={_isOpusMethod != null}, audioLen={audio.Length}";
                }
            }
            catch (Exception ex)
            {
                _status = $"Play error: {ex.Message}";
                _log.Warning($"[DEBUG] Play error: {ex}");
            }
        }

        private static void ResolveOpus()
        {
            if (_opusResolved) return;
            _opusResolved = true;

            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _opusType = asm.GetType("Dissonance.Audio.Codecs.Opus.OpusAudioUtility"); } catch { }
                    if (_opusType != null) break;
                }
                if (_opusType == null) return;

                _isOpusMethod = _opusType.GetMethod("IsOpusData", BindingFlags.Public | BindingFlags.Static);
                _toClipMethod = _opusType.GetMethod("ToAudioClip", BindingFlags.Public | BindingFlags.Static);
            }
            catch { }
        }

        // ===================== SAVE / LOAD =====================

        private void SaveAllEvents(List<SpeechEvent> events)
        {
            if (events == null || events.Count == 0) { _status = "Nothing to save."; return; }

            try
            {
                string path = GetDebugPath();
                if (path == null) { _status = "Cannot determine save path."; return; }
                Directory.CreateDirectory(Path.GetDirectoryName(path));

                int saved = 0;
                long totalAudio = 0;

                using (var ms = new MemoryStream())
                using (var bw = new BinaryWriter(ms))
                {
                    // First pass: serialize valid events
                    var valid = new List<(byte[] meta, byte[] audio)>();
                    foreach (var ev in events)
                    {
                        byte[] meta = ReplayableSndEvent.GetDataFromSndEvent(ev);
                        if (meta != null && meta.Length > 0)
                        {
                            byte[] audio = ev.CompressedAudioData ?? Array.Empty<byte>();
                            valid.Add((meta, audio));
                        }
                    }

                    // v2 format: [count][metaLen][meta][audioLen][audio]...
                    bw.Write(valid.Count);
                    foreach (var (meta, audio) in valid)
                    {
                        bw.Write(meta.Length);
                        bw.Write(meta);
                        bw.Write(audio.Length);
                        bw.Write(audio);
                        totalAudio += audio.Length;
                        saved++;
                    }

                    File.WriteAllBytes(path, ms.ToArray());
                }

                var fi = new FileInfo(path);
                _status = $"Saved {saved} events! file={fi.Length / 1024}KB, audio={totalAudio / 1024}KB";
                _log.Msg($"[DEBUG] Saved {saved} events to {path} ({fi.Length}B, audio={totalAudio}B)");
            }
            catch (Exception ex)
            {
                _status = $"Save error: {ex.Message}";
            }
        }

        private void LoadEventsFromFile()
        {
            try
            {
                string path = GetDebugPath();
                if (path == null || !File.Exists(path))
                {
                    _status = "No debug file found. Save first!";
                    return;
                }

                byte[] data = File.ReadAllBytes(path);
                var loaded = new List<SpeechEvent>();
                int withAudio = 0;
                int withoutAudio = 0;

                using (var ms = new MemoryStream(data))
                using (var br = new BinaryReader(ms))
                {
                    int count = br.ReadInt32();
                    for (int i = 0; i < count; i++)
                    {
                        if (ms.Position >= data.Length) break;

                        int metaLen = br.ReadInt32();
                        if (metaLen <= 0 || ms.Position + metaLen > data.Length) continue;
                        byte[] meta = br.ReadBytes(metaLen);

                        int audioLen = br.ReadInt32();
                        byte[] audio = (audioLen > 0 && ms.Position + audioLen <= data.Length)
                            ? br.ReadBytes(audioLen) : null;

                        // Deserialize
                        var wrapper = new ReplayableSndEvent(SndEventType.PLAYER, 0, 0, 0, meta, null);
                        var ev = wrapper.GetSndEvent();
                        if (ev == null) continue;

                        // Inject audio
                        if (audio != null && audio.Length > 0 && AudioDataField != null)
                        {
                            AudioDataField.SetValue(ev, audio);
                            withAudio++;
                        }
                        else
                        {
                            withoutAudio++;
                        }

                        loaded.Add(ev);
                    }
                }

                _loadedEvents = loaded;
                _selectedLoadedIdx = -1;
                _status = $"Loaded {loaded.Count} events ({withAudio} with audio, {withoutAudio} without)";
                _log.Msg($"[DEBUG] Loaded {loaded.Count} events from {path} ({withAudio} with audio, {withoutAudio} without)");
            }
            catch (Exception ex)
            {
                _status = $"Load error: {ex.Message}";
            }
        }

        private string GetDebugPath()
        {
            try
            {
                var mgr = MonoSingleton<PlatformMgr>.Instance;
                if (mgr != null)
                {
                    string folder = mgr.GetSaveFileFolderPath();
                    if (!string.IsNullOrEmpty(folder))
                        return Path.Combine(folder, "MimesisData", DEBUG_FILE);
                }
            }
            catch { }
            return Path.Combine(Application.persistentDataPath, "MimesisData", DEBUG_FILE);
        }

        // ===================== INPUT SYSTEM =====================

        private static bool WasKeyPressedThisFrame()
        {
            if (_inputSystemFailed) return false;

            if (!_inputSystemReady)
            {
                try
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (_keyboardType == null)
                            _keyboardType = asm.GetType("UnityEngine.InputSystem.Keyboard");
                        if (_keyType == null)
                            _keyType = asm.GetType("UnityEngine.InputSystem.Key");
                        if (_keyboardType != null && _keyType != null) break;
                    }

                    if (_keyboardType == null || _keyType == null)
                    { _inputSystemFailed = true; return false; }

                    _keyboardCurrentProp = _keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                    _keyboardIndexer = _keyboardType.GetProperty("Item", new[] { _keyType });
                    _f9KeyValue = Enum.Parse(_keyType, "F9");
                    _inputSystemReady = true;
                }
                catch { _inputSystemFailed = true; return false; }
            }

            object keyboard = _keyboardCurrentProp?.GetValue(null);
            if (keyboard == null) return false;
            object keyControl = _keyboardIndexer?.GetValue(keyboard, new[] { _f9KeyValue });
            if (keyControl == null) return false;
            var prop = keyControl.GetType().GetProperty("wasPressedThisFrame", BindingFlags.Public | BindingFlags.Instance);
            return prop != null && (bool)prop.GetValue(keyControl);
        }
    }
}
#endif
