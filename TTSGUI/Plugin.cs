using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Photon.Voice.Unity;

namespace TTSGUI
{
    [BepInPlugin(PluginInfo.GUID, PluginInfo.Name, PluginInfo.Version)]
    public class Manager : BaseUnityPlugin
    {
        bool barOpen;
        string barText = "";
        float smoothAnim;
        bool prevKey;
        Vector3 frozenPos;
        Texture2D overlay;
        bool isSpeaking;
        Recorder cachedRecorder;
        AudioSource localAudio;
        Process ttsProc;

        static readonly string TempDir = Path.GetTempPath();
        static string ttsExePath;

        void Awake()
        {
            try
            {
                overlay = new Texture2D(1, 1);

                localAudio = gameObject.AddComponent<AudioSource>();
                localAudio.spatialBlend = 0f;
                localAudio.volume = 1f;

                ttsExePath = Path.Combine(TempDir, "QuickTTS.exe");
                
                bool needsExtraction = true;
                if (File.Exists(ttsExePath))
                {
                    try
                    {
                        File.Delete(ttsExePath);
                    }
                    catch (IOException)
                    {
                        needsExtraction = false;
                    }
                    catch (System.UnauthorizedAccessException)
                    {
                        needsExtraction = false;
                    }
                }

                if (needsExtraction)
                {
                    using Stream stream = Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("TTSGUI.Resources.QuickTTS.exe");
                    if (stream != null)
                    {
                        using var fs = new FileStream(ttsExePath, FileMode.Create, FileAccess.Write);
                        stream.CopyTo(fs);
                    }
                    else
                    {
                        UnityEngine.Debug.LogError("[TTSGUI] Failed to load QuickTTS.exe resource");
                    }
                }

                StartTTSProcess();
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[TTSGUI] Error in Awake: {e.Message}\n{e.StackTrace}");
            }
        }

        void StartTTSProcess()
        {
            ttsProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ttsExePath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };
            ttsProc.Start();
        }

        void OnDestroy()
        {
            try
            {
                if (ttsProc != null && !ttsProc.HasExited)
                {
                    ttsProc.StandardInput.Close();
                    ttsProc.Kill();
                }
            }
            catch { }
        }

        void OnGUI()
        {
            smoothAnim = barOpen
                ? Mathf.Lerp(smoothAnim, 0.5f, Time.deltaTime)
                : Mathf.Lerp(smoothAnim, 0f, Time.deltaTime);

            if (Mathf.Floor(smoothAnim * 255f) != 0f)
            {
                overlay.SetPixel(0, 0, new Color(0f, 0f, 0f, smoothAnim));
                overlay.Apply();
                GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), overlay);

                GUIStyle textStyle = new GUIStyle(GUI.skin.textArea) { fontSize = 50 };
                GUI.SetNextControlName("bartext");
                barText = GUI.TextArea(
                    new Rect(10, smoothAnim * 115 - 50, Screen.width - 20, 50),
                    barText, textStyle
                );

                if (isSpeaking)
                {
                    GUIStyle statusStyle = new GUIStyle(GUI.skin.label)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 24
                    };
                    statusStyle.normal.textColor = new Color(0.5f, 1f, 0.5f);
                    GUI.Label(new Rect(0, smoothAnim * 115 + 10, Screen.width, 30), "Speaking", statusStyle);
                }
            }

            if (barOpen)
            {
                if (GorillaTagger.Instance != null)
                    GorillaTagger.Instance.transform.position = frozenPos;
                GUI.FocusControl("bartext");

                if (barText.Contains("\n"))
                {
                    string msg = barText.Replace("\n", "").Trim();
                    GUI.FocusControl(null);
                    ToggleBar();
                    if (msg.Length > 0 && !isSpeaking)
                        StartCoroutine(Speak(msg));
                }
            }

            bool down = UnityInput.Current.GetKey(KeyCode.Slash) && !UnityInput.Current.GetKey(KeyCode.LeftShift);
            if (down && !prevKey)
                ToggleBar();
            prevKey = down;
        }

        void ToggleBar()
        {
            barOpen = !barOpen;
            barText = "";
            if (barOpen && GorillaTagger.Instance != null)
                frozenPos = GorillaTagger.Instance.transform.position;
            else
                GUI.FocusControl(null);
        }

        Recorder GetRecorder()
        {
            if (cachedRecorder != null)
                return cachedRecorder;
            cachedRecorder = FindAnyObjectByType<Recorder>();
            return cachedRecorder;
        }

        IEnumerator Speak(string text)
        {
            isSpeaking = true;

            if (ttsProc == null || ttsProc.HasExited)
                StartTTSProcess();

            ttsProc.StandardInput.WriteLine(text);
            ttsProc.StandardInput.Flush();

            var readTask = Task.Run(() => ttsProc.StandardOutput.ReadLine());
            while (!readTask.IsCompleted)
                yield return null;

            string outPath = readTask.Result;
            if (string.IsNullOrEmpty(outPath) || !File.Exists(outPath))
            {
                isSpeaking = false;
                yield break;
            }

            string fileUrl = "file:///" + outPath.Replace("\\", "/");
            using (var req = UnityWebRequestMultimedia.GetAudioClip(fileUrl, AudioType.WAV))
            {
                yield return req.SendWebRequest();
                if (req.result == UnityWebRequest.Result.ConnectionError || req.result == UnityWebRequest.Result.ProtocolError)
                {
                    isSpeaking = false;
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
                if (clip == null || clip.length <= 0f)
                {
                    isSpeaking = false;
                    yield break;
                }

                localAudio.clip = clip;
                localAudio.Play();

                var recorder = GetRecorder();
                if (recorder != null)
                {
                    var prevSource = recorder.SourceType;
                    bool wasTransmitting = recorder.TransmitEnabled;

                    recorder.TransmitEnabled = true;
                    recorder.SourceType = Recorder.InputSourceType.AudioClip;
                    recorder.AudioClip = clip;
                    recorder.LoopAudioClip = false;
                    recorder.RestartRecording();

                    yield return new WaitForSeconds(clip.length + 0.25f);

                    recorder.SourceType = prevSource;
                    recorder.AudioClip = null;
                    recorder.TransmitEnabled = wasTransmitting;
                    recorder.RestartRecording();
                }
                else
                {
                    yield return new WaitForSeconds(clip.length);
                }
            }

            try { File.Delete(outPath); } catch { }
            isSpeaking = false;
        }
    }
}
