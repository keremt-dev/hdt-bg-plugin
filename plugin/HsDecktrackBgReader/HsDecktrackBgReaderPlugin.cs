using System;
using System.IO;
using System.Reflection;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Plugins;

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
        private EntityDumper _dumper;
        private LocalHttpServer _server;
        private BgStateExtractor _extractor;

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
                DumpGameSnapshot("OnGameStart");
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
                DumpGameSnapshot("OnTurnStart:" + player);
                RefreshLobbyState("OnTurnStart:" + player);
            }
            catch (Exception ex)
            {
                SafeLog("OnTurnStart failed", ex);
            }
        }

        private void OnModeChanged()
        {
            try
            {
                var game = Hearthstone_Deck_Tracker.Core.Game;
                _dumper.Write("event_OnModeChanged", new
                {
                    currentMode = SafeProp(game, "CurrentGameMode"),
                    currentGameType = SafeProp(game, "CurrentGameType"),
                    isBattlegrounds = SafeProp(game, "IsBattlegroundsMatch"),
                });
                DumpGameSnapshot("OnModeChanged");
                RefreshLobbyState("OnModeChanged");
            }
            catch (Exception ex)
            {
                SafeLog("OnModeChanged failed", ex);
            }
        }

        private void RefreshLobbyState(string trigger)
        {
            try
            {
                var json = _extractor?.TryBuildJson();
                _server?.SetState(json);
                _dumper?.Write("lobby_state_pushed", new { trigger, hasState = json != null, length = json?.Length ?? 0 });
            }
            catch (Exception ex)
            {
                SafeLog("RefreshLobbyState failed", ex);
            }
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
