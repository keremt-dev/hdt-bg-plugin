using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;

// In Release the VerboseDump const is false, so the `if (VerboseDump) ...`
// branches are unreachable by design. We don't want to read those warnings
// every time we build Release.
#pragma warning disable 162    // unreachable code

namespace HsDecktrackBgReader
{
    public class HsDecktrackBgReaderPlugin : IPlugin
    {
        public string Name => "HS Decktrack BG Reader (spike)";
        public string Description => "Dumps BG hero-selection entities to disk so we can identify which tags carry tribe bans and offered heroes. Stage-1 spike.";
        public string ButtonText => "Dump now";
        public string Author => "hs-decktrack";
        public Version Version => new Version(0, 1, 0);
        public MenuItem MenuItem => null;

        private const int HttpPort = 9876;
        private const int PollIntervalMs = 1500;

        // Verbose dumping (full entity snapshots, reflection probes, BG view
        // model catalogues, deep race probe) was the whole point of the
        // spike. Release builds don't need it — the extractor knows its
        // targets — and the JSONL would grow to tens of MB per session.
#if DEBUG
        private const bool VerboseDump = true;
#else
        private const bool VerboseDump = false;
#endif
        private EntityDumper _dumper;
        private LocalHttpServer _server;
        private BgStateExtractor _extractor;
        private Timer _poller;
        private string _lastServedJson;       // last non-null snapshot we pushed
        private int _pollerBusy;              // 0/1 sentinel — re-entrant Timer guard

        public void OnLoad()
        {
            try
            {
                var dumpDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HsDecktrackBgReader");
                Directory.CreateDirectory(dumpDir);
                _dumper = new EntityDumper(dumpDir);

                _dumper.Write("plugin_load", new { version = Version.ToString(), dumpDir });

                _extractor = new BgStateExtractor(_dumper);
                _server = new LocalHttpServer(HttpPort);
                _server.Start();
                _dumper.Write("http_server_started", new { port = HttpPort });

                GameEvents.OnGameStart.Add(OnGameStart);
                GameEvents.OnTurnStart.Add(OnTurnStart);
                GameEvents.OnModeChanged.Add(OnModeChanged);

                _dumper.Write("subscribed_events", new { events = new[] { "OnGameStart", "OnTurnStart", "OnModeChanged" } });

                // The HDT events above do NOT fire during the BG hero-pick
                // window, which is exactly when the user wants Live data.
                // Drive a small poll loop instead — it just re-runs the
                // extractor; it does not write the verbose entity dump.
                _poller = new Timer(_ => PollTick(), null, PollIntervalMs, PollIntervalMs);
                _dumper.Write("poll_timer_started", new { intervalMs = PollIntervalMs });

                RefreshLobbyState("OnLoad");
            }
            catch (Exception ex)
            {
                SafeLog("OnLoad failed", ex);
            }
        }

        public void OnUnload()
        {
            try
            {
                _dumper?.Write("plugin_unload", new { });
                _poller?.Dispose();
                _server?.Dispose();
                _dumper?.Dispose();
            }
            catch (Exception ex)
            {
                SafeLog("OnUnload failed", ex);
            }
        }

        public void OnButtonPress()
        {
            try
            {
                _dumper?.Write("manual_dump_button", new { });
                // The button is a developer affordance — always dump verbose
                // state regardless of build configuration when it's pressed.
                DumpGameSnapshot("manual_button");
                RefreshLobbyState("manual_button");
            }
            catch (Exception ex)
            {
                SafeLog("OnButtonPress failed", ex);
            }
        }

        public void OnUpdate()
        {
            // Called every game-loop tick. We do NOT dump here — too noisy.
            // Real periodic snapshots are driven by OnTurnStart in the spike.
        }

        private void OnGameStart()
        {
            try
            {
                _dumper.Write("event_OnGameStart", new { });
                if (VerboseDump) DumpGameSnapshot("OnGameStart");
                RefreshLobbyState("OnGameStart");
            }
            catch (Exception ex)
            {
                SafeLog("OnGameStart failed", ex);
            }
        }

        private void OnTurnStart(Hearthstone_Deck_Tracker.Enums.ActivePlayer player)
        {
            try
            {
                _dumper.Write("event_OnTurnStart", new { player = player.ToString() });
                if (VerboseDump) DumpGameSnapshot("OnTurnStart:" + player);
                RefreshLobbyState("OnTurnStart:" + player);
            }
            catch (Exception ex)
            {
                SafeLog("OnTurnStart failed", ex);
            }
        }

        private void OnModeChanged(Hearthstone_Deck_Tracker.Enums.Hearthstone.Mode mode)
        {
            try
            {
                var game = Hearthstone_Deck_Tracker.Core.Game;
                var modeName = mode.ToString();
                _dumper.Write("event_OnModeChanged", new
                {
                    mode = modeName,
                    currentMode = SafeProp(game, "CurrentGameMode"),
                    currentGameType = SafeProp(game, "CurrentGameType"),
                    isBattlegrounds = SafeProp(game, "IsBattlegroundsMatch"),
                });
                if (VerboseDump) DumpGameSnapshot("OnModeChanged:" + modeName);
                // BG starts at BACON (queue/lobby) and continues into GAMEPLAY.
                // When we leave both, drop the cached snapshot so the browser
                // doesn't show a stale lobby from a previous match.
                if (modeName != "BACON" && modeName != "GAMEPLAY")
                {
                    ClearServedState("OnModeChanged:" + modeName);
                }
                else
                {
                    RefreshLobbyState("OnModeChanged:" + modeName);
                }
            }
            catch (Exception ex)
            {
                SafeLog("OnModeChanged failed", ex);
            }
        }

        private void PollTick()
        {
            // Single-flight: skip if a previous tick is still running.
            if (Interlocked.Exchange(ref _pollerBusy, 1) == 1) return;
            try { RefreshLobbyState("timer"); }
            catch (Exception ex) { SafeLog("PollTick failed", ex); }
            finally { Interlocked.Exchange(ref _pollerBusy, 0); }
        }

        /// <summary>
        /// Try a fresh extraction. If it produces JSON, push it AND remember
        /// it as the last good snapshot. If it produces null, keep whatever
        /// the server is already serving — a missed read shouldn't blank out
        /// a perfectly valid lobby state. Only ClearServedState wipes the
        /// snapshot, and that only fires when we leave BG mode.
        /// </summary>
        private void RefreshLobbyState(string trigger)
        {
            try
            {
                var json = _extractor?.TryBuildJson();
                if (json == null) return;
                if (json == _lastServedJson) return;          // no diff, save bandwidth
                _lastServedJson = json;
                _server?.SetState(json);
                _dumper?.Write("lobby_state_pushed", new { trigger, length = json.Length });
            }
            catch (Exception ex)
            {
                SafeLog("RefreshLobbyState failed", ex);
            }
        }

        private void ClearServedState(string reason)
        {
            _extractor?.ResetLobby();
            if (_lastServedJson == null) return;
            _lastServedJson = null;
            _server?.SetState(null);
            _dumper?.Write("lobby_state_cleared", new { reason });
        }

        private void DumpGameSnapshot(string trigger)
        {
            try
            {
                var game = Hearthstone_Deck_Tracker.Core.Game;
                _dumper.WriteEntitySnapshot(trigger, game);
                _dumper.WriteReflectionProbe(trigger, game);
                _dumper.WriteBgViewModelProbe(trigger);
            }
            catch (Exception ex)
            {
                SafeLog("DumpGameSnapshot failed for " + trigger, ex);
            }
        }

        private static object SafeProp(object target, string name)
        {
            try
            {
                if (target == null) return null;
                var p = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                return p?.GetValue(target);
            }
            catch { return null; }
        }

        private void SafeLog(string label, Exception ex)
        {
            try
            {
                _dumper?.Write("error", new { label, message = ex.Message, type = ex.GetType().FullName, stack = ex.StackTrace });
            }
            catch { }
        }
    }
}
