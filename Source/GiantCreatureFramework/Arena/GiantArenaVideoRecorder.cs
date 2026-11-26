using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using RimWorld;
using UnityEngine;
using UnityStubs.Rendering;
using Verse;

namespace GiantCreatureFramework
{
    public sealed class GiantArenaRecordingRequest
    {
        public string OutputPath { get; set; } = string.Empty;
        public string FfmpegPath { get; set; } = string.Empty;
        public int Fps { get; set; } = 60;
        public int TicksPerFrame { get; set; } = 1;
        public float IntroSeconds { get; set; } = 1.5f;
        public float MaxMatchSeconds { get; set; } = 120f;
        public float PostKoSeconds { get; set; } = 5.0f;
        public bool TrackCamera { get; set; } = true;
        public float CameraSmooth { get; set; } = 0.07f;
        public float CameraPadding { get; set; } = 10.5f;
        public float CameraMinSize { get; set; } = 16f;
        public float CameraMaxSize { get; set; } = 44f;
        public int Crf { get; set; } = 17;
    }

    public sealed class GiantArenaRecordingStatus
    {
        public bool Recording { get; set; }
        public bool Finished { get; set; }
        public bool Succeeded { get; set; }
        public string OutputPath { get; set; } = string.Empty;
        public int Fps { get; set; }
        public int TicksPerFrame { get; set; }
        public float PlaybackSpeed { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public int FramesCaptured { get; set; }
        public int FramesEncoded { get; set; }
        public float RecordedSeconds { get; set; }
        public int PostKoFramesRemaining { get; set; }
        public string MatchStatus { get; set; } = string.Empty;
        public string WinnerSide { get; set; } = string.Empty;
        public float SideAHp { get; set; }
        public float SideBHp { get; set; }
        public string Error { get; set; } = string.Empty;
    }

    /// <summary>
    /// High-framerate (60fps native 1.00x) in-process Vulkan/Waylith framebuffer-to-FFmpeg
    /// video recorder for Giant Arena matches. Reads raw top-down RGBA directly from
    /// ScreenCaptureBackend.Current and streams to ffmpeg stdin on a background thread with
    /// zero PNG encoding overhead and zero temporary disk frames.
    /// </summary>
    public static class GiantArenaVideoRecorder
    {
        private static readonly object StateLock = new object();

        private static bool recording;
        private static bool finished;
        private static bool succeeded;
        private static string lastError = string.Empty;

        private static GiantArenaRecordingRequest activeReq;
        private static bool waitingForReadback;
        private static int missedReadbackFrames;
        private static int introFramesRemaining;
        private static int battleFramesCaptured;
        private static int maxBattleFrames;
        private static int postKoFramesRemaining = -1;
        private static int totalFramesCaptured;
        private static int totalFramesEncoded;

        private static int frameWidth;
        private static int frameHeight;
        private static int expectedFrameBytes;

        private static BlockingCollection<byte[]> frameQueue;
        private static Thread encoderThread;
        private static Process ffmpegProc;
        private static string ffmpegStderrTail = string.Empty;

        public static bool IsRecording => recording;
        public static event Action<int, float, Map> OnRecorderFrameStepped;

        private static string ResolveFfmpegPath(string requestedPath)
        {
            string[] candidates =
            {
                requestedPath,
                Environment.GetEnvironmentVariable("GCF_FFMPEG_PATH"),
                Environment.GetEnvironmentVariable("FFMPEG_PATH"),
                "ffmpeg.exe",
                "ffmpeg"
            };
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] pathDirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

            foreach (string raw in candidates)
            {
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }
                string candidate = raw.Trim().Trim('"');
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
                if (candidate.IndexOf(Path.DirectorySeparatorChar) < 0 && candidate.IndexOf(Path.AltDirectorySeparatorChar) < 0)
                {
                    foreach (string dir in pathDirs)
                    {
                        try
                        {
                            string full = Path.Combine(dir.Trim(), candidate);
                            if (File.Exists(full))
                            {
                                return full;
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            return string.Empty;
        }

        public static bool StartRecording(GiantArenaRecordingRequest req, out string error)
        {
            error = string.Empty;
            if (recording)
            {
                error = "A native arena recording is already in progress.";
                return false;
            }
            if (!GiantArenaService.IsArenaActive)
            {
                error = "Giant Arena is not active. Call setup_match before start_native_recording.";
                return false;
            }
            if (ScreenCaptureBackend.Current == null)
            {
                error = "UnityStubs.Rendering.ScreenCaptureBackend.Current is null (headless backend cannot capture frames).";
                return false;
            }
            if (req == null || string.IsNullOrWhiteSpace(req.OutputPath))
            {
                error = "OutputPath is required.";
                return false;
            }
            string ffmpegPath = ResolveFfmpegPath(req.FfmpegPath);
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                error = string.IsNullOrWhiteSpace(req.FfmpegPath)
                    ? "ffmpeg executable not found. Pass FfmpegPath, set GCF_FFMPEG_PATH or FFMPEG_PATH, or add ffmpeg to PATH."
                    : $"ffmpeg executable not found at '{req.FfmpegPath}'.";
                return false;
            }

            string outDir = Path.GetDirectoryName(req.OutputPath);
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            req.FfmpegPath = ffmpegPath;
            req.Fps = Mathf.Clamp(req.Fps, 15, 60);
            req.TicksPerFrame = Mathf.Clamp(req.TicksPerFrame, 1, 12);
            req.IntroSeconds = Mathf.Clamp(req.IntroSeconds, 0f, 10f);
            req.MaxMatchSeconds = Mathf.Clamp(req.MaxMatchSeconds, 5f, 600f);
            req.PostKoSeconds = Mathf.Clamp(req.PostKoSeconds, 0f, 30f);
            req.CameraSmooth = Mathf.Clamp(req.CameraSmooth, 0.01f, 1.0f);
            req.Crf = Mathf.Clamp(req.Crf, 12, 28);

            // Pause RimWorld's realtime tick loop so we advance strictly ticksPerFrame per rendered frame
            if (Find.TickManager != null)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }
            GiantBossHealthBarHUD.ManualStepMode = true;

            // Snap initial camera cleanly on both fighters
            GiantArenaService.PurgeArenaSpeechBubblesNow();
            GiantArenaService.ResetCameraSmoothing();
            GiantArenaService.FrameMatchCamera(
                smoothFactor: 1.0f,
                paddingCells: req.CameraPadding,
                minRootSize: req.CameraMinSize,
                maxRootSize: req.CameraMaxSize,
                dtSeconds: 1f / Mathf.Max(1, req.Fps));

            lock (StateLock)
            {
                activeReq = req;
                recording = true;
                finished = false;
                succeeded = false;
                lastError = string.Empty;
                ffmpegStderrTail = string.Empty;
                waitingForReadback = false;
                missedReadbackFrames = 0;
                introFramesRemaining = Mathf.RoundToInt(req.IntroSeconds * req.Fps);
                battleFramesCaptured = 0;
                maxBattleFrames = Mathf.Max(1, Mathf.RoundToInt(req.MaxMatchSeconds * req.Fps));
                postKoFramesRemaining = -1;
                totalFramesCaptured = 0;
                totalFramesEncoded = 0;
                frameWidth = 0;
                frameHeight = 0;
                expectedFrameBytes = 0;
                frameQueue = new BlockingCollection<byte[]>(boundedCapacity: 24);
                encoderThread = null;
                ffmpegProc = null;
            }

            float speed = (req.TicksPerFrame * req.Fps) / 60f;
            Log.Message(
                $"[GCF_ARENA_REC] Started native recording -> '{req.OutputPath}' " +
                $"({req.Fps}fps, {req.TicksPerFrame} tick/frame = {speed:F2}x real-time, " +
                $"intro={req.IntroSeconds:F1}s, postKO={req.PostKoSeconds:F1}s, max={req.MaxMatchSeconds:F0}s)");
            return true;
        }

        public static void StopRecording(bool waitForFlush = true)
        {
            if (!recording && encoderThread == null)
            {
                return;
            }
            FinishRecordingInternal(markSuccess: string.IsNullOrEmpty(lastError), waitForFlush: waitForFlush);
        }

        public static void ForceAbortForReset()
        {
            if (recording || encoderThread != null)
            {
                FinishRecordingInternal(markSuccess: false, waitForFlush: false);
            }
        }

        public static GiantArenaRecordingStatus GetStatus()
        {
            GiantArenaMatchState match = GiantArenaService.GetMatchState();
            lock (StateLock)
            {
                int fps = activeReq?.Fps ?? 60;
                int tpf = activeReq?.TicksPerFrame ?? 1;
                return new GiantArenaRecordingStatus
                {
                    Recording = recording,
                    Finished = finished,
                    Succeeded = succeeded,
                    OutputPath = activeReq?.OutputPath ?? string.Empty,
                    Fps = fps,
                    TicksPerFrame = tpf,
                    PlaybackSpeed = (tpf * fps) / 60f,
                    FrameWidth = frameWidth,
                    FrameHeight = frameHeight,
                    FramesCaptured = totalFramesCaptured,
                    FramesEncoded = totalFramesEncoded,
                    RecordedSeconds = fps > 0 ? (float)totalFramesCaptured / fps : 0f,
                    PostKoFramesRemaining = postKoFramesRemaining,
                    MatchStatus = match.Status,
                    WinnerSide = match.WinnerSide,
                    SideAHp = match.SideATotalHealthPercent,
                    SideBHp = match.SideBTotalHealthPercent,
                    Error = lastError
                };
            }
        }

        /// <summary>
        /// Called once per Unity Update frame from GameComponent_GiantArena.GameComponentUpdate().
        /// Advances simulation lockstep by exactly ticksPerFrame, smoothly tracks camera,
        /// and requests GPU swapchain capture for the current rendered frame.
        /// </summary>
        internal static void OnUpdateFrame()
        {
            if (!recording || activeReq == null)
            {
                return;
            }

            if (!GiantArenaService.IsArenaActive || Find.CurrentMap == null)
            {
                FailRecording("Arena became inactive during recording.");
                return;
            }

            // Ensure RimWorld stays paused between our lockstep ticks
            if (Find.TickManager != null && Find.TickManager.CurTimeSpeed != TimeSpeed.Paused)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            }

            if (waitingForReadback)
            {
                missedReadbackFrames++;
                if (missedReadbackFrames >= 6)
                {
                    // Re-arm capture request if a render frame skipped swapchain readback
                    missedReadbackFrames = 0;
                    ScreenCaptureBackend.Current?.RequestCapture();
                }
                return;
            }

            // Check if we already finished all intro + battle + post-KO frames
            if (postKoFramesRemaining == 0 || (postKoFramesRemaining < 0 && battleFramesCaptured >= maxBattleFrames))
            {
                FinishRecordingInternal(markSuccess: true, waitForFlush: true);
                return;
            }

            // Advance HUD animations smoothly by exact 1/fps seconds
            float frameDt = 1f / Mathf.Max(1, activeReq.Fps);
            Find.CurrentMap?.GetComponent<GiantFootprintMapComponent>()?.BossHealthBarHud?.QueueManualStepSeconds(frameDt);

            if (introFramesRemaining > 0)
            {
                // Hold dramatic opening face-off while Versus Boss Health Bar unfolds smoothly
                if (activeReq.TrackCamera)
                {
                    GiantArenaService.FrameMatchCamera(
                        smoothFactor: 1.0f,
                        paddingCells: activeReq.CameraPadding,
                        minRootSize: activeReq.CameraMinSize,
                        maxRootSize: activeReq.CameraMaxSize,
                        dtSeconds: frameDt);
                }
            }
            else
            {
                // Lockstep simulation advance: exactly ticksPerFrame ticks per rendered frame
                TickManager tm = Find.TickManager;
                if (tm != null)
                {
                    for (int t = 0; t < activeReq.TicksPerFrame; t++)
                    {
                        tm.DoSingleTick();
                    }
                }
                GiantArenaService.SyncPawnTweenersForLockstepFrame(Find.CurrentMap, activeReq.TicksPerFrame);
                try
                {
                    OnRecorderFrameStepped?.Invoke(
                        totalFramesCaptured,
                        (float)totalFramesCaptured / Mathf.Max(1, activeReq.Fps),
                        Find.CurrentMap);
                }
                catch
                {
                }

                if (activeReq.TrackCamera)
                {
                    GiantArenaService.FrameMatchCamera(
                        smoothFactor: activeReq.CameraSmooth,
                        paddingCells: activeReq.CameraPadding,
                        minRootSize: activeReq.CameraMinSize,
                        maxRootSize: activeReq.CameraMaxSize,
                        dtSeconds: frameDt);
                }
            }

            IScreenCaptureBackend backend = ScreenCaptureBackend.Current;
            if (backend == null)
            {
                FailRecording("ScreenCaptureBackend.Current became null.");
                return;
            }

            if (backend.RequestCapture())
            {
                waitingForReadback = true;
                missedReadbackFrames = 0;
            }
        }

        /// <summary>
        /// Called from Harmony Postfix on UnityEngine.ScreenCapture.WritePendingScreenshots
        /// right after Waylith finishes rendering the current frame (world + giants + Versus HUD).
        /// </summary>
        internal static void OnEndOfRenderFrame()
        {
            if (!recording || !waitingForReadback || activeReq == null)
            {
                return;
            }

            IScreenCaptureBackend backend = ScreenCaptureBackend.Current;
            if (backend == null)
            {
                FailRecording("ScreenCaptureBackend.Current became null during readback.");
                return;
            }

            if (!backend.TryReadScreen(out byte[] rgba, out int width, out int height))
            {
                return;
            }

            waitingForReadback = false;
            missedReadbackFrames = 0;

            if (rgba == null || width <= 0 || height <= 0)
            {
                return;
            }

            if (encoderThread == null)
            {
                if (!StartEncoderProcessAndThread(width, height, out string encErr))
                {
                    FailRecording(encErr);
                    return;
                }
            }

            if (rgba.Length != expectedFrameBytes)
            {
                if (frameWidth > 0 && frameHeight > 0 && rgba.Length == width * height * 4)
                {
                    byte[] resized = new byte[expectedFrameBytes];
                    for (int y = 0; y < frameHeight; y++)
                    {
                        int sy = (y * height) / frameHeight;
                        int srcRow = sy * width * 4;
                        int dstRow = y * frameWidth * 4;
                        for (int x = 0; x < frameWidth; x++)
                        {
                            int sx = (x * width) / frameWidth;
                            Buffer.BlockCopy(rgba, srcRow + sx * 4, resized, dstRow + x * 4, 4);
                        }
                    }
                    rgba = resized;
                }
                else
                {
                    return;
                }
            }

            try
            {
                frameQueue?.Add(rgba);
            }
            catch (Exception ex)
            {
                FailRecording("Encoder queue failed: " + ex.Message);
                return;
            }

            totalFramesCaptured++;

            if (introFramesRemaining > 0)
            {
                introFramesRemaining--;
            }
            else
            {
                battleFramesCaptured++;
                GiantArenaMatchState st = GiantArenaService.GetMatchState();
                if (postKoFramesRemaining < 0 && (st.Status == nameof(GiantArenaMatchStatus.Finished) || st.Status == nameof(GiantArenaMatchStatus.Draw)))
                {
                    postKoFramesRemaining = Mathf.RoundToInt(activeReq.PostKoSeconds * activeReq.Fps);
                    Log.Message(
                        $"[GCF_ARENA_REC] Match concluded (status={st.Status}, winner={st.WinnerSide}) at frame {totalFramesCaptured}; " +
                        $"recording {postKoFramesRemaining} post-KO frames ({activeReq.PostKoSeconds:F1}s)...");
                }
                else if (postKoFramesRemaining > 0)
                {
                    postKoFramesRemaining--;
                }
            }
        }

        private static bool StartEncoderProcessAndThread(int width, int height, out string error)
        {
            error = string.Empty;
            try
            {
                frameWidth = width;
                frameHeight = height;
                expectedFrameBytes = width * height * 4;

                string args =
                    $"-y -f rawvideo -pix_fmt rgba -s {width}x{height} -r {activeReq.Fps} -i - " +
                    $"-vf \"scale=trunc(iw/2)*2:trunc(ih/2)*2\" " +
                    $"-c:v libx264 -preset fast -crf {activeReq.Crf} -pix_fmt yuv420p -movflags +faststart " +
                    $"\"{activeReq.OutputPath}\"";

                var psi = new ProcessStartInfo
                {
                    FileName = activeReq.FfmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                ffmpegProc = Process.Start(psi);
                if (ffmpegProc == null)
                {
                    error = "Failed to start ffmpeg process.";
                    return false;
                }

                // Drain stderr asynchronously so ffmpeg never deadlocks on a full stderr pipe
                ffmpegProc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        ffmpegStderrTail = e.Data;
                    }
                };
                ffmpegProc.BeginErrorReadLine();

                BlockingCollection<byte[]> localQueue = frameQueue;
                Process localProc = ffmpegProc;

                encoderThread = new Thread(() =>
                {
                    try
                    {
                        Stream stdin = localProc.StandardInput.BaseStream;
                        foreach (byte[] frame in localQueue.GetConsumingEnumerable())
                        {
                            stdin.Write(frame, 0, frame.Length);
                            Interlocked.Increment(ref totalFramesEncoded);
                        }
                        stdin.Flush();
                        localProc.StandardInput.Close();
                    }
                    catch (Exception ex)
                    {
                        lock (StateLock)
                        {
                            if (string.IsNullOrEmpty(lastError))
                            {
                                lastError = "FFmpeg pipe write error: " + ex.Message;
                            }
                        }
                    }
                })
                {
                    IsBackground = true,
                    Name = "GCF_ArenaFFmpegEncoder"
                };
                encoderThread.Start();
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed starting native FFmpeg encoder: " + ex.Message;
                return false;
            }
        }

        private static void FailRecording(string reason)
        {
            lock (StateLock)
            {
                lastError = reason;
            }
            Log.Error("[GCF_ARENA_REC] " + reason);
            FinishRecordingInternal(markSuccess: false, waitForFlush: false);
        }

        private static void FinishRecordingInternal(bool markSuccess, bool waitForFlush)
        {
            BlockingCollection<byte[]> qToClose;
            Thread threadToWait;
            Process procToWait;

            lock (StateLock)
            {
                recording = false;
                waitingForReadback = false;
                qToClose = frameQueue;
                threadToWait = encoderThread;
                procToWait = ffmpegProc;
                frameQueue = null;
                encoderThread = null;
                ffmpegProc = null;
            }

            GiantBossHealthBarHUD.ManualStepMode = false;

            try { qToClose?.CompleteAdding(); } catch { }

            if (waitForFlush)
            {
                try { threadToWait?.Join(TimeSpan.FromSeconds(60)); } catch { }
                try
                {
                    if (procToWait != null && !procToWait.HasExited)
                    {
                        procToWait.WaitForExit(60000);
                    }
                }
                catch { }
            }
            else
            {
                try
                {
                    if (procToWait != null && !procToWait.HasExited)
                    {
                        procToWait.Kill();
                    }
                }
                catch { }
            }

            bool ok = markSuccess && string.IsNullOrEmpty(lastError);
            if (procToWait != null && procToWait.HasExited && procToWait.ExitCode != 0 && ok)
            {
                ok = false;
                lock (StateLock)
                {
                    lastError = $"ffmpeg exited with code {procToWait.ExitCode}: {ffmpegStderrTail}";
                }
            }

            try { procToWait?.Dispose(); } catch { }
            try { qToClose?.Dispose(); } catch { }

            lock (StateLock)
            {
                succeeded = ok;
                finished = true;
            }

            Log.Message(
                $"[GCF_ARENA_REC] Finished native recording: ok={ok} frames={totalFramesCaptured} " +
                $"encoded={totalFramesEncoded} file='{activeReq?.OutputPath}' err='{lastError}'");
        }
    }
}
